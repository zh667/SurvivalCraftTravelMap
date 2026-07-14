using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class ExplorationRecorderTests
{
    [Fact]
    public void Chunk_coverage_requires_all_256_opaque_pixels_and_repairs_after_recording()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path);
        var chunk = new TerrainChunkCoordinate(2, -3);
        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        var tile = store.GetOrLoad(coordinate.TileX, coordinate.TileZ);
        var recorder = CreateRecorder(
            new FakeTerrainMapSource(defaultContent: 1),
            store,
            new Rgba32(10, 20, 30, 255));

        var diagnosticsBeforeUnknownQuery = store.Diagnostics;
        Assert.False(recorder.IsChunkFullyExplored(chunk));
        Assert.Equal(diagnosticsBeforeUnknownQuery, store.Diagnostics);
        tile.SetRegion(
            coordinate.LocalX,
            coordinate.LocalZ,
            TerrainChunkCoordinate.Size,
            TerrainChunkCoordinate.Size,
            Enumerable.Repeat(
                new Rgba32(1, 2, 3, 255),
                TerrainChunkCoordinate.PixelCount).ToArray());
        tile.SetPixel(coordinate.LocalX + 15, coordinate.LocalZ + 15, default);
        Assert.False(recorder.IsChunkFullyExplored(chunk));

        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));
        Assert.True(recorder.IsChunkFullyExplored(chunk));
    }

    [Fact]
    public void Chunk_coverage_uses_floor_tiles_for_negative_chunk_coordinates()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path);
        var recorder = CreateRecorder(
            new FakeTerrainMapSource(defaultContent: 1),
            store,
            new Rgba32(10, 20, 30, 255));
        var chunk = new TerrainChunkCoordinate(-1, -4);

        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));

        Assert.True(recorder.IsChunkFullyExplored(chunk));
        Assert.True(store.ContainsKnownTile(-1, -1));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(63, 0, 2, 1)]
    [InlineData(0, 63, 1, 2)]
    public void Region_coverage_rejects_invalid_bounds(int x, int z, int width, int height)
    {
        var tile = new MapTile(0, 0);

        Assert.ThrowsAny<ArgumentOutOfRangeException>(
            () => tile.IsRegionFullyExplored(x, z, width, height));
    }

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
    public void Footprint_sequence_records_four_atomic_chunks_and_preserves_an_unreadable_target()
    {
        using var directory = new TemporaryDirectory();
        var expected = new Rgba32(10, 20, 30, 255);
        var store = new ExplorationTileStore(directory.Path);
        var ready = new FakeTerrainMapSource(defaultContent: 1, isReady: true);
        var recorder = CreateRecorder(ready, store, expected);
        var scheduler = new TerrainChunkExplorationScheduler();
        var footprint = MinimapExplorationFootprint.Create(16f, 16f, 32, 1f);
        scheduler.ObserveFootprint(footprint);

        foreach (var chunk in scheduler.GetPendingAttempts(4))
        {
            Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));
            scheduler.MarkCompleted(chunk);
        }

        Assert.Equal(0, scheduler.PendingCount);
        foreach (var chunk in footprint.ChunksNearestFirst)
        {
            var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
            AssertRegion(
                store.GetOrLoad(coordinate.TileX, coordinate.TileZ),
                coordinate.LocalX,
                coordinate.LocalZ,
                expected);
        }

        var unreadable = new FakeTerrainMapSource(defaultContent: 1, isReady: false);
        var unreadableRecorder = CreateRecorder(unreadable, store, expected);
        var next = MinimapExplorationFootprint.Create(40f, 8f, 16, 1f);
        scheduler.ObserveFootprint(next);
        var pending = scheduler.GetPendingAttempts(1)[0];
        Assert.Equal(ExplorationRecordResult.NotReady, unreadableRecorder.RecordChunk(pending));
        Assert.Equal(1, scheduler.PendingCount);
        var pendingCoordinate = TileCoordinate.FromWorld(pending.OriginX, pending.OriginZ);
        Assert.False(store.GetOrLoad(pendingCoordinate.TileX, pendingCoordinate.TileZ).TryGetPixel(
            pendingCoordinate.LocalX,
            pendingCoordinate.LocalZ,
            out _));
    }

    [Fact]
    public void Unchanged_footprint_identity_skips_rebuild_and_observation_while_pending_attempts_continue()
    {
        var scheduler = new TerrainChunkExplorationScheduler();
        MinimapExplorationFootprintIdentity? observedIdentity = null;
        var materializedCount = 0;
        var observationCount = 0;

        IReadOnlyList<TerrainChunkCoordinate> Update(float x, float z)
        {
            var footprintIdentity = MinimapExplorationFootprintIdentity.Create(x, z, 16, 1f);
            if (observedIdentity != footprintIdentity)
            {
                observedIdentity = footprintIdentity;
                materializedCount++;
                var footprint = MinimapExplorationFootprint.Create(footprintIdentity);
                scheduler.ObserveFootprint(footprint);
                observationCount++;
            }

            return scheduler.GetPendingAttempts(4);
        }

        var firstAttempts = Update(4f, 4f);
        var movedAttempts = Update(4.25f, 4.25f);

        Assert.Equal(1, materializedCount);
        Assert.Equal(1, observationCount);
        Assert.Equal(4, scheduler.PendingCount);
        Assert.Equal(firstAttempts, movedAttempts);
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
