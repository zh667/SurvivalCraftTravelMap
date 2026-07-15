using System.IO.Compression;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class ExplorationTileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "SurvivalcraftTravelMap.Tests",
        Guid.NewGuid().ToString("N"));

    public ExplorationTileStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Mutation_lease_dispose_is_idempotent_and_publishes_the_completed_write()
    {
        var store = new ExplorationTileStore(_directory, capacity: 1);
        var lease = store.AcquireMutation(0, 0);
        lease.Tile.SetPixel(4, 5, new Rgba32(10, 20, 30, 255));

        lease.Dispose();
        lease.Dispose();
        store.GetOrLoad(9, 9);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloaded = new ExplorationTileStore(_directory).GetOrLoad(0, 0);
        Assert.True(reloaded.TryGetPixel(4, 5, out var color));
        Assert.Equal(new Rgba32(10, 20, 30, 255), color);
    }

    [Fact]
    public async Task Inflight_flush_cannot_evict_pinned_lease_and_dispose_publishes_a_new_generation()
    {
        var store = new ExplorationTileStore(_directory, capacity: 1);
        var lease = store.AcquireMutation(0, 0);
        var pinnedTile = lease.Tile;
        var expected = new Rgba32(10, 20, 30, 255);
        pinnedTile.SetPixel(4, 5, expected);

        await store.FlushAsync(TestContext.Current.CancellationToken);
        var diagnosticsAfterFlush = store.Diagnostics;
        Assert.Equal(
            TileMutationAdmission.Pressure,
            store.TryAcquireMutation(9, 9, out var pressuredLease));
        Assert.Null(pressuredLease);
        Assert.Equal(diagnosticsAfterFlush, store.Diagnostics);
        Assert.True(store.IsUnderPressure);
        Assert.Same(pinnedTile, store.GetOrLoad(0, 0));

        lease.Dispose();

        Assert.True(store.IsUnderPressure);
        Assert.Equal(
            TileMutationAdmission.Pressure,
            store.TryAcquireMutation(9, 9, out pressuredLease));
        Assert.Null(pressuredLease);

        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.False(store.IsUnderPressure);
        Assert.Equal(
            TileMutationAdmission.Acquired,
            store.TryAcquireMutation(9, 9, out var admittedLease));
        using (admittedLease!)
        {
            Assert.NotSame(pinnedTile, store.GetOrLoad(0, 0));
        }

        var reloaded = new ExplorationTileStore(_directory).GetOrLoad(0, 0);
        Assert.True(reloaded.TryGetPixel(4, 5, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Using_releases_admitted_lease_when_region_mutation_throws()
    {
        var store = new ExplorationTileStore(_directory, capacity: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var lease = store.AcquireMutation(0, 0);
            lease.Tile.SetRegion(
                x: 60,
                z: 60,
                TerrainChunkCoordinate.Size,
                TerrainChunkCoordinate.Size,
                new Rgba32[TerrainChunkCoordinate.PixelCount]);
        });

        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            TileMutationAdmission.Acquired,
            store.TryAcquireMutation(9, 9, out var admittedLease));
        admittedLease!.Dispose();
        Assert.False(store.IsUnderPressure);
    }

    [Fact]
    public async Task Recorded_chunk_persists_all_256_cells_with_current_codec_version()
    {
        var expected = new Rgba32(10, 20, 30, 255);
        var store = new ExplorationTileStore(_directory);
        var recorder = new ExplorationRecorder(
            new TerrainMapSampler(
                new FakeTerrainMapSource(defaultContent: 1),
                TerrainMapSamplerTests.CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
                {
                    [1] = new BlockPixelData(1, expected, false),
                })),
            store);

        var result = recorder.RecordChunk(new TerrainChunkCoordinate(-1, 5));
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExplorationRecordResult.Recorded, result);
        var path = Path.Combine(_directory, "-1_1.sctm");
        using (var source = File.OpenRead(path))
        using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
        {
            Span<byte> header = stackalloc byte[5];
            deflate.ReadExactly(header);
            Assert.Equal("SCTM"u8.ToArray(), header[..4].ToArray());
            Assert.Equal(3, header[4]);
        }

        var reloaded = new ExplorationTileStore(_directory).GetOrLoad(-1, 1);
        for (var z = 16; z < 32; z++)
        {
            for (var x = 48; x < 64; x++)
            {
                Assert.True(reloaded.TryGetPixel(x, z, out var actual));
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public async Task Flush_atomically_persists_a_dirty_tile()
    {
        var store = new ExplorationTileStore(_directory, capacity: 2);
        var tile = store.GetOrLoad(-7, 11);
        var versionBeforeMutation = store.GetTileMutationVersion(-7, 11);
        tile.SetPixel(5, 6, new Rgba32(10, 20, 30, 255));
        store.MarkDirty(tile);

        Assert.True(store.GetTileMutationVersion(-7, 11) > versionBeforeMutation);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var path = Path.Combine(_directory, "-7_11.sctm");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        var reloaded = new ExplorationTileStore(_directory, capacity: 2).GetOrLoad(-7, 11);
        Assert.True(reloaded.TryGetPixel(5, 6, out var color));
        Assert.Equal(new Rgba32(10, 20, 30, 255), color);
    }

    [Fact]
    public void Corrupt_tile_is_isolated_and_replaced_with_an_unexplored_tile()
    {
        var path = Path.Combine(_directory, "2_-3.sctm");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var store = new ExplorationTileStore(_directory, capacity: 2);

        var tile = store.GetOrLoad(2, -3);

        Assert.Equal(2, tile.TileX);
        Assert.Equal(-3, tile.TileZ);
        Assert.False(tile.TryGetPixel(0, 0, out _));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Tile_with_mismatched_embedded_coordinates_is_isolated()
    {
        var path = Path.Combine(_directory, "2_-3.sctm");
        using (var stream = File.Create(path))
        {
            TileCodec.Write(stream, new MapTile(20, -30));
        }

        var tile = new ExplorationTileStore(_directory, capacity: 2).GetOrLoad(2, -3);

        Assert.Equal(2, tile.TileX);
        Assert.Equal(-3, tile.TileZ);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public async Task Dirty_tile_is_not_evicted_until_a_flush_succeeds()
    {
        var store = new ExplorationTileStore(_directory, capacity: 2);
        var dirty = store.GetOrLoad(0, 0);
        dirty.SetPixel(0, 0, new Rgba32(1, 2, 3, 4));
        store.MarkDirty(dirty);
        store.GetOrLoad(1, 0);
        store.GetOrLoad(2, 0);

        Assert.Same(dirty, store.GetOrLoad(0, 0));

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.FlushAsync(cancellation.Token));
        }

        store.GetOrLoad(3, 0);
        Assert.Same(dirty, store.GetOrLoad(0, 0));

        await store.FlushAsync(TestContext.Current.CancellationToken);
        store.GetOrLoad(3, 0);
        store.GetOrLoad(4, 0);

        var loadedAgain = store.GetOrLoad(0, 0);
        Assert.NotSame(dirty, loadedAgain);
        Assert.True(loadedAgain.TryGetPixel(0, 0, out _));
    }

    [Fact]
    public async Task Concurrent_flushes_are_serialized_without_sharing_a_temp_file()
    {
        var store = new ExplorationTileStore(_directory, capacity: 2);
        var tile = store.GetOrLoad(0, 0);
        var random = new Random(1729);
        for (var z = 0; z < MapTile.Size; z++)
        {
            for (var x = 0; x < MapTile.Size; x++)
            {
                tile.SetPixel(
                    x,
                    z,
                    new Rgba32(
                        (byte)random.Next(256),
                        (byte)random.Next(256),
                        (byte)random.Next(256),
                        (byte)random.Next(256)));
            }
        }

        store.MarkDirty(tile);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = store.FlushAsync(cancellationToken);
        var second = store.FlushAsync(cancellationToken);
        await Task.WhenAll(first, second);

        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(_directory, "0_0.sctm")));
    }

    [Fact]
    public void Defaults_are_a_five_second_flush_interval_and_128_tiles()
    {
        var store = new ExplorationTileStore(_directory);

        Assert.Equal(128, store.Capacity);
        Assert.Equal(TimeSpan.FromSeconds(5), store.FlushInterval);
    }

    [Fact]
    public async Task Persistent_write_failure_applies_bounded_backpressure_and_recovers_after_flush()
    {
        var failWrites = true;
        var store = new ExplorationTileStore(
            _directory,
            capacity: 2,
            flushInterval: TimeSpan.FromSeconds(5),
            async (path, tile, token) =>
            {
                if (failWrites)
                {
                    throw new IOException("disk full");
                }

                await AtomicFile.ReplaceAsync(
                    path,
                    (stream, _) =>
                    {
                        TileCodec.Write(stream, tile);
                        return Task.CompletedTask;
                    },
                    token);
            });

        var recorder = new ExplorationRecorder(
            new TerrainMapSampler(
                new FakeTerrainMapSource(defaultContent: 1),
                TerrainMapSamplerTests.CreatePixelData()),
            store);
        for (var tileX = 0; tileX < 40; tileX++)
        {
            recorder.RecordChunk(new TerrainChunkCoordinate(tileX * 4, 0));
        }

        Assert.True(store.IsUnderPressure);
        Assert.True(store.Diagnostics.CachedTileCount <= store.Capacity);
        await Assert.ThrowsAsync<IOException>(() => store.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ExplorationRecordResult.Pressure,
            recorder.RecordChunk(new TerrainChunkCoordinate(99 * 4, 0)));

        failWrites = false;
        await store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            ExplorationRecordResult.Recorded,
            recorder.RecordChunk(new TerrainChunkCoordinate(99 * 4, 0)));
        Assert.False(store.IsUnderPressure);
        Assert.True(store.Diagnostics.CachedTileCount <= store.Capacity);
    }

    [Fact]
    public void World_keys_normalize_inputs_and_use_24_uppercase_hex_characters()
    {
        var local = WorldKey.ForLocal(@"C:\Worlds\MyWorld\\");
        var normalizedLocal = WorldKey.ForLocal(@"c:\worlds\myworld");
        var server = WorldKey.ForServer("EXAMPLE.COM/", 25565, "WORLD-A\\");
        var normalizedServer = WorldKey.ForServer("example.com", 25565, "world-a");

        Assert.Equal(normalizedLocal, local);
        Assert.Equal(normalizedServer, server);
        Assert.Matches("^[0-9A-F]{24}$", local);
        Assert.Matches("^[0-9A-F]{24}$", server);
        Assert.NotEqual(local, server);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
