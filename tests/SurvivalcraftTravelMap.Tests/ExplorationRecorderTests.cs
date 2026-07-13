using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class ExplorationRecorderTests
{
    [Fact]
    public async Task Ready_chunk_writes_all_256_cells_once_and_releases_its_mutation_lease()
    {
        using var directory = new TemporaryDirectory();
        var expected = new Rgba32(10, 20, 30, 255);
        var source = new FakeTerrainMapSource(defaultContent: 1);
        var store = new ExplorationTileStore(directory.Path, capacity: 1);
        var recorder = CreateRecorder(source, store, expected);
        var chunk = new TerrainChunkCoordinate(-1, 5);
        var tile = store.GetOrLoad(-1, 1);
        var versionBefore = tile.Version;

        var result = recorder.RecordChunk(chunk);

        Assert.Equal(ExplorationRecordResult.Recorded, result);
        Assert.Equal(versionBefore + 1, tile.Version);
        AssertRegion(tile, localX: 48, localZ: 16, expected);
        Assert.False(tile.TryGetPixel(47, 16, out _));

        await store.FlushAsync(TestContext.Current.CancellationToken);
        store.GetOrLoad(9, 9);
        var reloaded = store.GetOrLoad(-1, 1);
        Assert.NotSame(tile, reloaded);
        AssertRegion(reloaded, localX: 48, localZ: 16, expected);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Not_ready_or_transparent_chunk_preserves_existing_target_without_admission(
        bool isReady,
        int content)
    {
        using var directory = new TemporaryDirectory();
        var writer = new TileWriterProbe();
        var store = new ExplorationTileStore(
            directory.Path,
            capacity: 1,
            flushInterval: null,
            writer.WriteAsync);
        var oldColor = new Rgba32(91, 92, 93, 255);
        using (var seed = store.AcquireMutation(0, 0))
        {
            seed.Tile.SetPixel(3, 4, oldColor);
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        var target = store.GetOrLoad(0, 0);
        var targetState = CaptureState(target);
        var diagnostics = store.Diagnostics;
        var pressure = store.IsUnderPressure;
        var path = Path.Combine(directory.Path, "0_0.sctm");
        var persistedBytes = File.ReadAllBytes(path);
        var writes = writer.WriteCount;
        var source = new FakeTerrainMapSource(defaultContent: content, isReady: isReady);
        var recorder = new ExplorationRecorder(
            new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData()),
            store);

        var result = recorder.RecordChunk(new TerrainChunkCoordinate(0, 0));
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExplorationRecordResult.NotReady, result);
        Assert.Equal(targetState, CaptureState(target));
        Assert.Equal(diagnostics, store.Diagnostics);
        Assert.Equal(pressure, store.IsUnderPressure);
        Assert.Equal(writes, writer.WriteCount);
        Assert.Equal(persistedBytes, File.ReadAllBytes(path));
        Assert.True(target.TryGetPixel(3, 4, out var actual));
        Assert.Equal(oldColor, actual);
    }

    [Fact]
    public async Task Storage_pressure_does_not_materialize_or_dirty_persisted_target()
    {
        using var directory = new TemporaryDirectory();
        var oldTargetColor = new Rgba32(41, 42, 43, 255);
        var seedStore = new ExplorationTileStore(directory.Path);
        var seededTarget = seedStore.GetOrLoad(0, 0);
        seededTarget.SetPixel(3, 4, oldTargetColor);
        seedStore.MarkDirty(seededTarget);
        await seedStore.FlushAsync(TestContext.Current.CancellationToken);
        var targetVersion = seededTarget.Version;
        var targetPath = Path.Combine(directory.Path, "0_0.sctm");
        var targetBytes = File.ReadAllBytes(targetPath);

        var writer = new TileWriterProbe();
        var store = new ExplorationTileStore(
            directory.Path,
            capacity: 1,
            flushInterval: null,
            writer.WriteAsync);
        using (var blocker = store.AcquireMutation(9, 9))
        {
            blocker.Tile.SetPixel(3, 4, new Rgba32(91, 92, 93, 255));
        }

        var blockerTile = store.GetOrLoad(9, 9);
        var blockerState = CaptureState(blockerTile);
        var diagnostics = store.Diagnostics;
        Assert.False(store.IsUnderPressure);
        var source = new FakeTerrainMapSource(defaultContent: 1);
        var recorder = CreateRecorder(source, store, new Rgba32(10, 20, 30, 255));

        var result = recorder.RecordChunk(new TerrainChunkCoordinate(0, 0));

        Assert.Equal(ExplorationRecordResult.Pressure, result);
        Assert.Equal(TerrainChunkCoordinate.PixelCount, source.SampledColumns.Count);
        Assert.Equal(diagnostics, store.Diagnostics);
        Assert.True(store.IsUnderPressure);
        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(blockerState, CaptureState(blockerTile));
        Assert.Equal(targetVersion, seededTarget.Version);
        Assert.Equal(targetBytes, File.ReadAllBytes(targetPath));

        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["9_9.sctm"], writer.WrittenFileNames);
        Assert.False(store.IsUnderPressure);
        Assert.Equal(targetBytes, File.ReadAllBytes(targetPath));
        var reloadedTarget = new ExplorationTileStore(directory.Path).GetOrLoad(0, 0);
        Assert.True(reloadedTarget.TryGetPixel(3, 4, out var actual));
        Assert.Equal(oldTargetColor, actual);
        Assert.False(reloadedTarget.TryGetPixel(0, 0, out _));
    }

    [Fact]
    public async Task Sampler_exception_propagates_without_mutation_and_leaves_no_tile_pinned()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path, capacity: 1);
        var tile = store.GetOrLoad(0, 0);
        tile.SetPixel(15, 15, new Rgba32(91, 92, 93, 255));
        store.MarkDirty(tile);
        var before = CaptureState(tile);
        var recorder = CreateRecorder(
            new ThrowingTerrainMapSource(throwAtSample: 4),
            store,
            new Rgba32(10, 20, 30, 255));

        var exception = Assert.Throws<TerrainSamplingException>(
            () => recorder.RecordChunk(new TerrainChunkCoordinate(0, 0)));

        Assert.Equal("sample 4", exception.Message);
        Assert.Equal(before, CaptureState(tile));

        await store.FlushAsync(TestContext.Current.CancellationToken);
        store.GetOrLoad(9, 9);
        var reloaded = store.GetOrLoad(0, 0);
        Assert.NotSame(tile, reloaded);
        Assert.Equal(before.Explored, CaptureState(reloaded).Explored);
        Assert.Equal(before.Colors, CaptureState(reloaded).Colors);
    }

    [Fact]
    public void Re_recording_overwrites_every_color_and_repairs_partial_transparent_legacy_region()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path);
        var tile = store.GetOrLoad(0, 0);
        var oldOpaque = new Rgba32(101, 102, 103, 255);
        var oldTransparent = new Rgba32(201, 202, 203, 0);
        for (var z = 16; z < 32; z++)
        {
            for (var x = 32; x < 48; x++)
            {
                if (((x + z) % 3) == 0)
                {
                    tile.SetPixel(x, z, oldOpaque);
                }
                else if (((x + z) % 3) == 1)
                {
                    tile.SetPixel(x, z, oldTransparent);
                }
            }
        }

        store.MarkDirty(tile);
        var versionBefore = tile.Version;
        var expected = new Rgba32(11, 22, 33, 255);
        var recorder = CreateRecorder(
            new FakeTerrainMapSource(defaultContent: 1),
            store,
            expected);

        var result = recorder.RecordChunk(new TerrainChunkCoordinate(2, 1));

        Assert.Equal(ExplorationRecordResult.Recorded, result);
        Assert.Equal(versionBefore + 1, tile.Version);
        AssertRegion(tile, localX: 32, localZ: 16, expected);
    }

    [Fact]
    public void Negative_tile_quadrants_use_0_16_32_48_offsets_and_each_chunk_stays_in_one_tile()
    {
        using var directory = new TemporaryDirectory();
        var expected = new Rgba32(10, 20, 30, 255);
        var store = new ExplorationTileStore(directory.Path);
        var recorder = CreateRecorder(
            new FakeTerrainMapSource(defaultContent: 1),
            store,
            expected);

        for (var quadrantZ = 0; quadrantZ < 4; quadrantZ++)
        {
            for (var quadrantX = 0; quadrantX < 4; quadrantX++)
            {
                var result = recorder.RecordChunk(
                    new TerrainChunkCoordinate(-4 + quadrantX, -8 + quadrantZ));

                Assert.Equal(ExplorationRecordResult.Recorded, result);
                Assert.Equal(1, store.Diagnostics.KnownTileCount);
                Assert.True(store.ContainsKnownTile(-1, -2));
                var tile = store.GetOrLoad(-1, -2);
                AssertRegion(
                    tile,
                    localX: quadrantX * TerrainChunkCoordinate.Size,
                    localZ: quadrantZ * TerrainChunkCoordinate.Size,
                    expected);
            }
        }

        var completedTile = store.GetOrLoad(-1, -2);
        Assert.Equal(16, completedTile.Version);
        for (var z = 0; z < MapTile.Size; z++)
        {
            for (var x = 0; x < MapTile.Size; x++)
            {
                Assert.True(completedTile.TryGetPixel(x, z, out var actual));
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public async Task Flush_snapshot_captured_before_RecordChunk_cannot_clear_its_later_generation()
    {
        using var directory = new TemporaryDirectory();
        var writer = new TileWriterProbe(blockFirstWrite: true);
        var store = new ExplorationTileStore(
            directory.Path,
            capacity: 1,
            flushInterval: null,
            writer.WriteAsync);
        var existing = store.GetOrLoad(0, 0);
        var oldInsideColor = new Rgba32(81, 82, 83, 255);
        var oldOutsideColor = new Rgba32(91, 92, 93, 255);
        existing.SetPixel(0, 0, oldInsideColor);
        existing.SetPixel(63, 63, oldOutsideColor);
        store.MarkDirty(existing);
        var expected = new Rgba32(10, 20, 30, 255);
        var recorder = CreateRecorder(
            new FakeTerrainMapSource(defaultContent: 1),
            store,
            expected);
        var versionBefore = existing.Version;
        var firstFlush = store.FlushAsync(TestContext.Current.CancellationToken);

        try
        {
            await writer.FirstWriteStarted.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                ExplorationRecordResult.Recorded,
                recorder.RecordChunk(new TerrainChunkCoordinate(0, 0)));
            Assert.Equal(versionBefore + 1, existing.Version);
        }
        finally
        {
            writer.ReleaseFirstWrite();
        }

        await firstFlush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var oldPersisted = new ExplorationTileStore(directory.Path).GetOrLoad(0, 0);
        Assert.True(oldPersisted.TryGetPixel(0, 0, out var oldActual));
        Assert.Equal(oldInsideColor, oldActual);
        Assert.Equal(
            TileMutationAdmission.Pressure,
            store.TryAcquireMutation(9, 9, out var pressuredLease));
        Assert.Null(pressuredLease);

        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloaded = new ExplorationTileStore(directory.Path).GetOrLoad(0, 0);
        AssertRegion(reloaded, 0, 0, expected);
        Assert.True(reloaded.TryGetPixel(63, 63, out var outsideActual));
        Assert.Equal(oldOutsideColor, outsideActual);
        Assert.Equal(2, writer.WriteCount);
        Assert.False(store.IsUnderPressure);
    }

    [Fact]
    public async Task Low_capacity_recording_persists_each_completed_chunk()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var store = new ExplorationTileStore(directory.Path, capacity: 4);
        var recorder = CreateRecorder(source, store, new Rgba32(10, 20, 30, 255));

        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(new TerrainChunkCoordinate(0, 0)));
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(new TerrainChunkCoordinate(4, 0)));
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(new TerrainChunkCoordinate(0, 4)));
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(new TerrainChunkCoordinate(4, 4)));
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["0_0.sctm", "0_1.sctm", "1_0.sctm", "1_1.sctm"],
            Directory.GetFiles(directory.Path, "*.sctm")
                .Select(path => Path.GetFileName(path)!)
                .Order()
                .ToArray());
    }

    [Fact]
    public async Task Low_capacity_sampler_failure_preserves_its_exception_and_existing_data()
    {
        using var directory = new TemporaryDirectory();
        var source = new ThrowingTerrainMapSource(throwAtSample: 4);
        var store = new ExplorationTileStore(directory.Path, capacity: 1);
        var existing = store.GetOrLoad(0, 0);
        var oldColor = new Rgba32(91, 92, 93, 255);
        existing.SetPixel(15, 15, oldColor);
        store.MarkDirty(existing);
        var recorder = CreateRecorder(source, store, new Rgba32(10, 20, 30, 255));

        var exception = Assert.Throws<TerrainSamplingException>(
            () => recorder.RecordChunk(new TerrainChunkCoordinate(0, 0)));
        Assert.Equal("sample 4", exception.Message);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloaded = new ExplorationTileStore(directory.Path).GetOrLoad(0, 0);
        Assert.True(reloaded.TryGetPixel(15, 15, out var actual));
        Assert.Equal(oldColor, actual);
        Assert.False(reloaded.TryGetPixel(0, 0, out _));
        Assert.False(reloaded.TryGetPixel(1, 0, out _));
        Assert.False(reloaded.TryGetPixel(2, 0, out _));
    }

    [Fact]
    public void Unavailable_or_transparent_column_is_not_explored_and_a_later_valid_sample_fills_it()
    {
        using var directory = new TemporaryDirectory();
        var source = new SwitchableTerrainMapSource();
        var store = new ExplorationTileStore(directory.Path);
        var recorder = new ExplorationRecorder(
            new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
            {
                [0] = new BlockPixelData(0, new Rgba32(0, 0, 0, 0), false),
                [1] = new BlockPixelData(1, new Rgba32(10, 20, 30, 255), false),
            })),
            store);

        recorder.RecordVisibleArea(centerX: 3, centerZ: 5, radius: 0);

        Assert.False(store.ContainsKnownTile(0, 0));
        Assert.False(store.GetOrLoad(0, 0).TryGetPixel(3, 5, out _));

        source.IsReady = true;
        source.Content = 1;
        recorder.RecordVisibleArea(centerX: 3, centerZ: 5, radius: 0);

        Assert.True(store.GetOrLoad(0, 0).TryGetPixel(3, 5, out var color));
        Assert.Equal(new Rgba32(10, 20, 30, 255), color);
    }

    [Fact]
    public void Recorder_reports_pressure_instead_of_growing_dirty_cache()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path, capacity: 1);
        var sampler = new TerrainMapSampler(
            new FakeTerrainMapSource(defaultContent: 1),
            TerrainMapSamplerTests.CreatePixelData());
        var recorder = new ExplorationRecorder(sampler, store);

        var first = recorder.RecordVisibleArea(0, 0, radius: 0);
        var pressure = recorder.RecordVisibleArea(MapTile.Size * 10, 0, radius: 0);

        Assert.Equal(ExplorationRecordResult.Recorded, first);
        Assert.Equal(ExplorationRecordResult.Pressure, pressure);
        Assert.Equal(1, store.Diagnostics.CachedTileCount);
    }

    [Fact]
    public async Task Visible_square_crossing_63_63_updates_and_marks_only_four_touched_tiles_dirty()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path);
        store.GetOrLoad(5, 5);
        var recorder = new ExplorationRecorder(sampler, store);

        recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["0_0.sctm", "0_1.sctm", "1_0.sctm", "1_1.sctm"],
            Directory.GetFiles(directory.Path, "*.sctm").Select(path => Path.GetFileName(path)!).Order().ToArray());

        Assert.True(store.GetOrLoad(0, 0).TryGetPixel(62, 62, out _));
        Assert.True(store.GetOrLoad(0, 1).TryGetPixel(62, 0, out _));
        Assert.True(store.GetOrLoad(1, 0).TryGetPixel(0, 62, out _));
        Assert.True(store.GetOrLoad(1, 1).TryGetPixel(0, 0, out _));
    }

    [Fact]
    public void Recorder_never_samples_outside_the_requested_visible_square()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var recorder = new ExplorationRecorder(sampler, new ExplorationTileStore(directory.Path));

        recorder.RecordVisibleArea(centerX: -10, centerZ: 20, radius: 2);

        Assert.Equal(25, source.SampledColumns.Count);
        Assert.All(source.SampledColumns, point => Assert.InRange(point.X, -12, -8));
        Assert.All(source.SampledColumns, point => Assert.InRange(point.Z, 18, 22));
        Assert.Equal(25, source.SampledColumns.Distinct().Count());
    }

    [Fact]
    public void Negative_radius_is_rejected_before_terrain_is_sampled()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource();
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var recorder = new ExplorationRecorder(sampler, new ExplorationTileStore(directory.Path));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => recorder.RecordVisibleArea(centerX: 0, centerZ: 0, radius: -1));
        Assert.Empty(source.SampledColumns);
    }

    private static ExplorationRecorder CreateRecorder(
        ITerrainMapSource source,
        ExplorationTileStore store,
        Rgba32 sampledColor)
    {
        return new ExplorationRecorder(
            new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
            {
                [1] = new BlockPixelData(1, sampledColor, false),
            })),
            store);
    }

    private static void AssertRegion(MapTile tile, int localX, int localZ, Rgba32 expected)
    {
        for (var z = 0; z < TerrainChunkCoordinate.Size; z++)
        {
            for (var x = 0; x < TerrainChunkCoordinate.Size; x++)
            {
                Assert.True(tile.TryGetPixel(localX + x, localZ + z, out var actual));
                Assert.Equal(expected, actual);
            }
        }
    }

    private static TileState CaptureState(MapTile tile)
    {
        var explored = new byte[MapTile.ExploredByteCount];
        var colors = new byte[MapTile.ColorByteCount];
        tile.CopyExploredTo(explored);
        tile.CopyColorsTo(colors);
        return new TileState(tile.Version, explored, colors);
    }

    private sealed record TileState(long Version, byte[] Explored, byte[] Colors)
    {
        public bool Equals(TileState? other) =>
            other is not null
            && Version == other.Version
            && Explored.AsSpan().SequenceEqual(other.Explored)
            && Colors.AsSpan().SequenceEqual(other.Colors);

        public override int GetHashCode() => HashCode.Combine(Version, Explored.Length, Colors.Length);
    }
}

internal sealed class SwitchableTerrainMapSource : ITerrainMapSource
{
    internal bool IsReady { get; set; }

    internal int Content { get; set; }

    public bool IsColumnReady(int x, int z) => IsReady;

    public int GetTopHeight(int x, int z) => 64;

    public int GetContent(int x, int y, int z) => Content;

    public int GetSeasonalTemperature(int x, int z) => 8;

    public int GetSeasonalHumidity(int x, int z) => 8;
}

internal sealed class TileWriterProbe(bool blockFirstWrite = false)
{
    private readonly object _sync = new();
    private readonly List<string> _writtenFileNames = [];
    private readonly TaskCompletionSource _firstWriteStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstWrite = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCount;

    internal int WriteCount => Volatile.Read(ref _writeCount);

    internal Task FirstWriteStarted => _firstWriteStarted.Task;

    internal string[] WrittenFileNames
    {
        get
        {
            lock (_sync)
            {
                return [.. _writtenFileNames];
            }
        }
    }

    internal void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();

    internal async Task WriteAsync(
        string path,
        MapTile tile,
        CancellationToken cancellationToken)
    {
        var write = Interlocked.Increment(ref _writeCount);
        lock (_sync)
        {
            _writtenFileNames.Add(Path.GetFileName(path));
        }

        if (blockFirstWrite && write == 1)
        {
            _firstWriteStarted.TrySetResult();
            await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
        }

        await AtomicFile.ReplaceAsync(
            path,
            (stream, _) =>
            {
                TileCodec.Write(stream, tile);
                return Task.CompletedTask;
            },
            cancellationToken);
    }
}

internal sealed class ThrowingTerrainMapSource(int throwAtSample) : ITerrainMapSource
{
    private int _sampleCount;

    public bool IsColumnReady(int x, int z) => true;

    public int GetTopHeight(int x, int z)
    {
        var sample = ++_sampleCount;
        if (sample == throwAtSample)
        {
            throw new TerrainSamplingException($"sample {sample}");
        }

        return 64;
    }

    public int GetContent(int x, int y, int z) => 1;

    public int GetSeasonalTemperature(int x, int z) => 8;

    public int GetSeasonalHumidity(int x, int z) => 8;
}

internal sealed class TerrainSamplingException(string message) : Exception(message);
