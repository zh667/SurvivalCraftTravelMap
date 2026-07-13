using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.Waypoints;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class WaypointRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "SurvivalcraftTravelMap.Tests",
        Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "waypoints.json");

    public WaypointRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Repository_preserves_exact_xyz_and_allows_duplicate_names()
    {
        var repository = CreateRepository();
        repository.Add("家", new Vector3(10.5f, 42.25f, -3.5f));
        repository.Add("家", new Vector3(20.5f, 70f, 9.5f));

        await repository.SaveAsync(TestContext.Current.CancellationToken);
        var loaded = CreateRepository();
        await loaded.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, loaded.GetAll().Count);
        Assert.Equal(new Vector3(10.5f, 42.25f, -3.5f), loaded.GetAll()[0].Position);
        Assert.Equal(new Vector3(20.5f, 70f, 9.5f), loaded.GetAll()[1].Position);
    }

    [Fact]
    public async Task Add_trims_unicode_names_and_preserves_stable_ids_and_utc_creation_times()
    {
        var repository = CreateRepository();
        var added = repository.Add("  家 / Café / 🚀  ", new Vector3(-0.25f, 64.125f, 9f));

        Assert.Equal("家 / Café / 🚀", added.Name);
        Assert.NotEqual(Guid.Empty, added.Id);
        Assert.Equal(TimeSpan.Zero, added.CreatedAt.Offset);

        await repository.SaveAsync(TestContext.Current.CancellationToken);
        var loaded = CreateRepository();
        await loaded.LoadAsync(TestContext.Current.CancellationToken);

        var reloaded = Assert.Single(loaded.GetAll());
        Assert.Equal(added, reloaded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Add_and_rename_reject_blank_names(string name)
    {
        var repository = CreateRepository();
        var waypoint = repository.Add("valid", Vector3.Zero);

        Assert.Throws<ArgumentException>(() => repository.Add(name, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => repository.Rename(waypoint.Id, name));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    public void Add_rejects_non_finite_coordinates(float x, float y, float z)
    {
        var repository = CreateRepository();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => repository.Add("invalid", new Vector3(x, y, z)));
    }

    [Fact]
    public void Rename_and_remove_report_not_found_and_preserve_other_entries()
    {
        var repository = CreateRepository();
        var first = repository.Add("first", new Vector3(1f, 2f, 3f));
        var second = repository.Add("second", new Vector3(4f, 5f, 6f));

        Assert.True(repository.Rename(first.Id, "  renamed  "));
        Assert.False(repository.Rename(Guid.NewGuid(), "missing"));
        Assert.True(repository.Remove(first.Id));
        Assert.False(repository.Remove(first.Id));
        Assert.False(repository.Remove(Guid.NewGuid()));

        var remaining = Assert.Single(repository.GetAll());
        Assert.Equal(second, remaining);
    }

    [Fact]
    public void Rename_changes_only_the_trimmed_name()
    {
        var repository = CreateRepository();
        var original = repository.Add("original", new Vector3(1.25f, -2.5f, 3.75f));

        Assert.True(repository.Rename(original.Id, "  renamed  "));

        var renamed = Assert.Single(repository.GetAll());
        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal("renamed", renamed.Name);
        Assert.Equal(original.Position, renamed.Position);
        Assert.Equal(original.CreatedAt, renamed.CreatedAt);
    }

    [Fact]
    public void GetAll_returns_a_snapshot_that_cannot_be_mutated_or_changed_by_later_writes()
    {
        var repository = CreateRepository();
        repository.Add("first", Vector3.Zero);
        var snapshot = repository.GetAll();

        repository.Add("second", Vector3.One);

        Assert.Single(snapshot);
        var list = Assert.IsAssignableFrom<IList<Waypoint>>(snapshot);
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Equal(2, repository.GetAll().Count);
    }

    [Fact]
    public async Task Repeated_load_replaces_memory_state_instead_of_duplicating_entries()
    {
        var repository = CreateRepository();
        repository.Add("only", new Vector3(1f, 2f, 3f));
        await repository.SaveAsync(TestContext.Current.CancellationToken);

        await repository.LoadAsync(TestContext.Current.CancellationToken);
        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(repository.GetAll());
    }

    [Fact]
    public async Task Save_uses_schema_version_one_and_invariant_json_under_non_invariant_culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            var repository = CreateRepository();
            repository.Add("中文, decimal", new Vector3(1.5f, 2.25f, -3.75f));

            await repository.SaveAsync(TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                FilePath,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.DoesNotContain("1,5", await File.ReadAllTextAsync(
                FilePath,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task Invalid_json_is_moved_to_a_unique_corrupt_file_without_overwriting_an_existing_one()
    {
        await File.WriteAllTextAsync(
            FilePath + ".corrupt",
            "keep me",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            FilePath,
            "{ definitely not json",
            TestContext.Current.CancellationToken);
        var repository = CreateRepository();

        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(repository.GetAll());
        Assert.False(repository.IsReadOnly);
        Assert.False(File.Exists(FilePath));
        Assert.Equal("keep me", await File.ReadAllTextAsync(
            FilePath + ".corrupt",
            TestContext.Current.CancellationToken));
        Assert.True(File.Exists(FilePath + ".corrupt.1"));
    }

    [Fact]
    public async Task Second_load_after_corruption_does_not_isolate_again()
    {
        await File.WriteAllTextAsync(
            FilePath,
            "not json",
            TestContext.Current.CancellationToken);
        var repository = CreateRepository();

        await repository.LoadAsync(TestContext.Current.CancellationToken);
        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(repository.GetAll());
        Assert.Equal(
            [FilePath + ".corrupt"],
            Directory.EnumerateFiles(_directory, "waypoints.json.corrupt*"));
    }

    [Fact]
    public async Task Unknown_schema_is_read_only_and_never_rewrites_the_file()
    {
        const string unknownJson = """
            {"schemaVersion":2,"waypoints":[{"future":"value"}]}
            """;
        await File.WriteAllTextAsync(FilePath, unknownJson, TestContext.Current.CancellationToken);
        var repository = CreateRepository();

        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(repository.IsReadOnly);
        Assert.Contains("2", repository.ReadOnlyError, StringComparison.Ordinal);
        Assert.Empty(repository.GetAll());
        Assert.Throws<InvalidOperationException>(() => repository.Add("blocked", Vector3.Zero));
        Assert.Throws<InvalidOperationException>(() => repository.Rename(Guid.NewGuid(), "blocked"));
        Assert.Throws<InvalidOperationException>(() => repository.Remove(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal(unknownJson, await File.ReadAllTextAsync(
            FilePath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_schema_outside_int_range_is_read_only_and_not_corrupt()
    {
        const string unknownJson = "{\"schemaVersion\":999999999999999999999999,\"waypoints\":[]}";
        await File.WriteAllTextAsync(FilePath, unknownJson, TestContext.Current.CancellationToken);
        var repository = CreateRepository();

        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(repository.IsReadOnly);
        Assert.Equal(unknownJson, await File.ReadAllTextAsync(
            FilePath,
            TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.corrupt*"));
    }

    [Fact]
    public async Task Save_waiting_behind_load_rechecks_read_only_state_before_writing()
    {
        const string unknownJson = "{\"schemaVersion\":2,\"waypoints\":[]}";
        await File.WriteAllTextAsync(FilePath, unknownJson, TestContext.Current.CancellationToken);
        var fileAccess = new ControlledReadFileAccess();
        var repository = new WaypointRepository(_directory, fileAccess);

        var load = repository.LoadAsync(TestContext.Current.CancellationToken);
        await fileAccess.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        var save = repository.SaveAsync(TestContext.Current.CancellationToken);
        fileAccess.AllowRead();

        await load;
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        Assert.Equal(unknownJson, await File.ReadAllTextAsync(
            FilePath,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("rename")]
    [InlineData("remove")]
    public async Task Crud_during_active_load_is_rejected_instead_of_silently_lost(string operation)
    {
        var seed = CreateRepository();
        seed.Add("disk", Vector3.Zero);
        await seed.SaveAsync(TestContext.Current.CancellationToken);
        var fileAccess = new ControlledReadFileAccess();
        var repository = new WaypointRepository(_directory, fileAccess);
        var memoryWaypoint = repository.Add("memory", Vector3.One);

        var load = repository.LoadAsync(TestContext.Current.CancellationToken);
        await fileAccess.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Throws<InvalidOperationException>(() => ApplyCrud(
                repository,
                memoryWaypoint.Id,
                operation));
            Assert.Equal(memoryWaypoint, Assert.Single(repository.GetAll()));
        }
        finally
        {
            fileAccess.AllowRead();
            await load;
        }
    }

    [Fact]
    public async Task Cancelled_load_clears_active_load_state()
    {
        var seed = CreateRepository();
        seed.Add("disk", Vector3.Zero);
        await seed.SaveAsync(TestContext.Current.CancellationToken);
        var fileAccess = new ControlledReadFileAccess();
        var repository = new WaypointRepository(_directory, fileAccess);
        using var cancellation = new CancellationTokenSource();

        var load = repository.LoadAsync(cancellation.Token);
        await fileAccess.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => repository.Add("blocked", Vector3.One));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.Equal("after cancellation", repository.Add("after cancellation", Vector3.One).Name);
    }

    [Fact]
    public async Task Failed_load_clears_active_load_state()
    {
        var seed = CreateRepository();
        seed.Add("disk", Vector3.Zero);
        await seed.SaveAsync(TestContext.Current.CancellationToken);
        var fileAccess = new ControlledReadFileAccess();
        var repository = new WaypointRepository(_directory, fileAccess);

        var load = repository.LoadAsync(TestContext.Current.CancellationToken);
        await fileAccess.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => repository.Add("blocked", Vector3.One));
        fileAccess.FailRead(new IOException("Controlled read failure."));
        await Assert.ThrowsAsync<IOException>(() => load);

        Assert.Equal("after failure", repository.Add("after failure", Vector3.One).Name);
    }

    [Fact]
    public async Task Invalid_schema_one_data_is_isolated_as_corrupt()
    {
        await File.WriteAllTextAsync(
            FilePath,
            "{\"schemaVersion\":1,\"waypoints\":null}",
            TestContext.Current.CancellationToken);
        var repository = CreateRepository();

        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(repository.GetAll());
        Assert.True(File.Exists(FilePath + ".corrupt"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"name\":\"point\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":3}]}")]
    public async Task Schema_one_missing_required_properties_is_isolated(string json)
    {
        await AssertSchemaOneIsolatedAsync(json);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[5]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":123,\"name\":\"point\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":[],\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":\"1\",\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":null,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":false,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":123}]}")]
    public async Task Schema_one_wrong_property_types_are_isolated(string json)
    {
        await AssertSchemaOneIsolatedAsync(json);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"one\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"},{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"two\",\"x\":4,\"y\":5,\"z\":6,\"createdAt\":\"2026-07-13T13:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"not-a-guid\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":3,\"createdAt\":\"not-a-time\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":\"NaN\",\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":\"Infinity\",\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":\"-Infinity\",\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1e9999,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":NaN,\"y\":2,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":Infinity,\"z\":3,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"waypoints\":[{\"id\":\"7a5033db-6c69-4777-9529-5b3d97597a2e\",\"name\":\"point\",\"x\":1,\"y\":2,\"z\":-Infinity,\"createdAt\":\"2026-07-13T12:00:00Z\"}]}")]
    public async Task Schema_one_invalid_values_are_isolated(string json)
    {
        await AssertSchemaOneIsolatedAsync(json);
    }

    [Fact]
    public async Task Cancelled_save_keeps_the_previous_file_intact()
    {
        var repository = CreateRepository();
        repository.Add("persisted", Vector3.Zero);
        await repository.SaveAsync(TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken);
        repository.Add("not saved", Vector3.One);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.SaveAsync(cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(
            FilePath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_save_keeps_the_previous_file_intact()
    {
        var repository = CreateRepository();
        repository.Add("persisted", Vector3.Zero);
        await repository.SaveAsync(TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken);
        repository.Add("not saved", Vector3.One);
        Directory.CreateDirectory(FilePath + ".tmp");

        var exception = await Record.ExceptionAsync(
            () => repository.SaveAsync(TestContext.Current.CancellationToken));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            FilePath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_saves_to_the_same_path_do_not_share_a_temp_file()
    {
        var first = CreateRepository();
        var second = CreateRepository();
        var longName = new string('界', 2048);
        for (var index = 0; index < 256; index++)
        {
            first.Add($"first-{index}-{longName}", new Vector3(index, index + 0.25f, -index));
            second.Add($"second-{index}-{longName}", new Vector3(-index, index + 0.5f, index));
        }

        await Task.WhenAll(
            first.SaveAsync(TestContext.Current.CancellationToken),
            second.SaveAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(FilePath + ".tmp"));
        var loaded = CreateRepository();
        await loaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(256, loaded.GetAll().Count);
    }

    [Fact]
    public async Task Concurrent_loads_and_save_use_one_lock_order_without_deadlock()
    {
        var seed = CreateRepository();
        seed.Add("persisted", Vector3.One);
        await seed.SaveAsync(TestContext.Current.CancellationToken);
        var firstLoad = CreateRepository();
        var secondLoad = CreateRepository();

        await Task.WhenAll(
                firstLoad.LoadAsync(TestContext.Current.CancellationToken),
                secondLoad.LoadAsync(TestContext.Current.CancellationToken),
                seed.SaveAsync(TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(firstLoad.GetAll());
        Assert.Single(secondLoad.GetAll());
    }

    [Fact]
    public async Task Waypoint_name_is_data_and_never_controls_the_storage_path()
    {
        var repository = CreateRepository();
        repository.Add("../../outside/\u4f4d\u7f6e", Vector3.Zero);

        await repository.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["waypoints.json"], Directory.EnumerateFiles(_directory).Select(Path.GetFileName));
    }

    private WaypointRepository CreateRepository() => new(_directory);

    private async Task AssertSchemaOneIsolatedAsync(string json)
    {
        await File.WriteAllTextAsync(FilePath, json, TestContext.Current.CancellationToken);
        var repository = CreateRepository();

        await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(repository.GetAll());
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.EnumerateFiles(_directory, "waypoints.json.corrupt*"));
    }

    private static void ApplyCrud(WaypointRepository repository, Guid id, string operation)
    {
        switch (operation)
        {
            case "add":
                repository.Add("added", Vector3.One);
                break;
            case "rename":
                repository.Rename(id, "renamed");
                break;
            case "remove":
                repository.Remove(id);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private sealed class ControlledReadFileAccess : IWaypointRepositoryFileAccess
    {
        private readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continueRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _readException;

        public Task ReadStarted => _readStarted.Task;

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            await _continueRead.Task.WaitAsync(cancellationToken);
            if (_readException is not null)
            {
                throw _readException;
            }

            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        public Task ReplaceAsync(
            string path,
            Func<Stream, CancellationToken, Task> writeAsync,
            CancellationToken cancellationToken) =>
            AtomicFile.ReplaceAsync(path, writeAsync, cancellationToken);

        public void Move(string sourcePath, string destinationPath) =>
            File.Move(sourcePath, destinationPath);

        public void AllowRead() => _continueRead.TrySetResult();

        public void FailRead(Exception exception)
        {
            _readException = exception;
            _continueRead.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
