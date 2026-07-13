using System.Collections.Concurrent;
using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.UI;
using SurvivalcraftTravelMap.Waypoints;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapRenderBudgetTests
{
    [Theory]
    [InlineData(32f)]
    [InlineData(2f)]
    public void Production_store_does_not_materialize_unknown_viewport_tiles(float blocksPerPixel)
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path, capacity: 128);
        var source = new TileStoreMapPixelSource(store);
        var transform = new MapTransform(
            Vector2.Zero,
            blocksPerPixel,
            new Vector2(1920f, 1080f));

        TravelMapRenderModel.RenderTerrain(source, transform, 1f, new CountingRenderSink());
        TravelMapRenderModel.RenderTerrain(source, transform, 1f, new CountingRenderSink());

        Assert.Equal(0, store.Diagnostics.TileMaterializations);
        Assert.Equal(0, store.Diagnostics.FileProbeCount);
        Assert.Equal(0, store.Diagnostics.DiskReadAttempts);
        Assert.Equal(0, source.SnapshotCloneCount);
    }

    [Theory]
    [InlineData(32f)]
    [InlineData(2f)]
    public async Task Production_store_bounds_known_tile_work_and_reuses_second_frame_snapshots(
        float blocksPerPixel)
    {
        using var directory = new TemporaryDirectory();
        var seed = new ExplorationTileStore(directory.Path, capacity: 512);
        for (var tileZ = -7; tileZ < 8; tileZ++)
        {
            for (var tileX = -10; tileX < 10; tileX++)
            {
                using var mutation = seed.AcquireMutation(tileX, tileZ);
                mutation.Tile.SetPixel(0, 0, new Rgba32(10, 20, 30, 255));
            }
        }

        await seed.FlushAsync(TestContext.Current.CancellationToken);
        var store = new ExplorationTileStore(directory.Path, capacity: 128);
        var source = new TileStoreMapPixelSource(store);
        var transform = new MapTransform(
            Vector2.Zero,
            blocksPerPixel,
            new Vector2(1920f, 1080f));

        var firstSink = new CapturingRenderSink();
        var firstStatistics = TravelMapRenderModel.RenderTerrain(source, transform, 1f, firstSink);
        var firstFrameDiagnostics = store.Diagnostics;
        var firstFrameClones = source.SnapshotCloneCount;
        TravelMapRenderModel.RenderTerrain(source, transform, 1f, new CountingRenderSink());

        Assert.Equal(300, store.Diagnostics.KnownTileCount);
        Assert.NotEmpty(firstSink.Terrain);
        Assert.InRange(
            firstStatistics.PixelQueries,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            firstStatistics.PrimitiveCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            firstFrameDiagnostics.TileMaterializations,
            1,
            TileStoreMapPixelSource.MaximumTilesPerFrame);
        Assert.Equal(
            firstFrameDiagnostics.TileMaterializations,
            firstFrameDiagnostics.FileProbeCount);
        Assert.Equal(
            firstFrameDiagnostics.TileMaterializations,
            firstFrameDiagnostics.DiskReadAttempts);
        Assert.InRange(firstFrameClones, 1, TileStoreMapPixelSource.MaximumTilesPerFrame);
        Assert.Equal(firstFrameDiagnostics.TileMaterializations, store.Diagnostics.TileMaterializations);
        Assert.Equal(firstFrameDiagnostics.FileProbeCount, store.Diagnostics.FileProbeCount);
        Assert.Equal(firstFrameDiagnostics.DiskReadAttempts, store.Diagnostics.DiskReadAttempts);
        Assert.Equal(firstFrameClones, source.SnapshotCloneCount);
    }

    [Theory]
    [InlineData(32f)]
    [InlineData(2f)]
    public void Full_hd_render_stays_within_the_global_sample_and_primitive_budget(float blocksPerPixel)
    {
        var source = new BudgetPixelSource(explored: true);
        var sink = new CountingRenderSink();

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, blocksPerPixel, new Vector2(1920f, 1080f)),
            1f,
            sink);

        Assert.InRange(statistics.PixelQueries, 1, TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(statistics.TerrainPrimitives, 1, TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.Equal(1, source.SessionCount);
    }

    [Fact]
    public void Downsampling_never_expands_one_explored_sample_over_unknown_world_blocks()
    {
        var source = new BudgetPixelSource(explored: false) { ExploredCoordinate = (0, 0) };
        var sink = new CapturingRenderSink();

        TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 32f, new Vector2(1920f, 1080f)),
            1f,
            sink);

        var cell = Assert.Single(sink.Terrain);
        Assert.InRange(cell.ScreenMaximum.X - cell.ScreenMinimum.X, 0f, (1f / 32f) + 0.001f);
        Assert.InRange(cell.ScreenMaximum.Y - cell.ScreenMinimum.Y, 0f, (1f / 32f) + 0.001f);
    }

    [Theory]
    [InlineData(float.MaxValue, float.MaxValue)]
    [InlineData(float.MinValue, float.MinValue)]
    [InlineData(2147483520f, -2147483520f)]
    public void Extreme_world_centers_finish_without_integer_overflow(float x, float z)
    {
        var source = new BudgetPixelSource(explored: false);

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(x, z), 32f, new Vector2(1920f, 1080f)),
            1f,
            new CountingRenderSink());

        Assert.InRange(statistics.PixelQueries, 0, TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
    }
}

public sealed class MapTileSnapshotTests
{
    [Fact]
    public void Transparent_pixels_from_an_old_tile_are_treated_as_unexplored()
    {
        const int x = 4;
        const int z = 7;
        var pixelIndex = (z * MapTile.Size) + x;
        var explored = new byte[MapTile.ExploredByteCount];
        explored[pixelIndex >> 3] |= (byte)(1 << (pixelIndex & 7));
        var tile = new MapTile(0, 0, explored, new byte[MapTile.ColorByteCount]);

        Assert.False(tile.TryGetPixel(x, z, out _));
        Assert.False(tile.CreateVersionedSnapshot().Snapshot.TryGetPixel(x, z, out _));
    }

    [Fact]
    public async Task Snapshot_sees_complete_old_or_new_region_under_concurrent_writes()
    {
        const int originX = 16;
        const int originZ = 32;
        const int width = 16;
        const int height = 16;
        var tile = new MapTile(0, 0);
        var oldColor = new Rgba32(1, 2, 3, 255);
        var newColor = new Rgba32(101, 102, 103, 255);
        var regions = new[]
        {
            Enumerable.Repeat(newColor, width * height).ToArray(),
            Enumerable.Repeat(oldColor, width * height).ToArray(),
        };
        tile.SetRegion(originX, originZ, width, height, regions[1]);
        var failures = new ConcurrentQueue<long>();
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 10_000; i++)
            {
                tile.SetRegion(originX, originZ, width, height, regions[i & 1]);
            }
        }, TestContext.Current.CancellationToken);

        start.Set();
        while (!writer.IsCompleted)
        {
            CheckSnapshot(tile.CreateVersionedSnapshot());
        }

        await writer;
        CheckSnapshot(tile.CreateVersionedSnapshot());
        Assert.Empty(failures);
        Assert.Equal(10_001, tile.Version);

        void CheckSnapshot(VersionedMapTileSnapshot captured)
        {
            var expected = (captured.Version & 1) == 0 ? newColor : oldColor;
            for (var localZ = 0; localZ < height; localZ++)
            {
                for (var localX = 0; localX < width; localX++)
                {
                    if (!captured.Snapshot.TryGetPixel(
                            originX + localX,
                            originZ + localZ,
                            out var actual)
                        || actual != expected)
                    {
                        failures.Enqueue(captured.Version);
                        return;
                    }
                }
            }
        }
    }

    [Fact]
    public void Static_tile_reuses_cached_immutable_snapshot_and_changed_tile_clones_once()
    {
        var tile = new MapTile(0, 0);
        tile.SetPixel(1, 1, new Rgba32(1, 2, 3, 4));
        var provider = new CountingTileProvider(tile);
        var source = new TileStoreMapPixelSource(provider, snapshotCapacity: 2);

        using (var first = source.BeginReadSession())
        {
            Assert.True(first.TryGetExploredPixel(1, 1, out _));
            Assert.True(first.TryGetExploredPixel(2, 2, out _) is false);
        }

        using (var second = source.BeginReadSession())
        {
            Assert.True(second.TryGetExploredPixel(1, 1, out _));
        }

        Assert.Equal(1, source.SnapshotCloneCount);
        tile.SetPixel(2, 2, new Rgba32(5, 6, 7, 8));
        using (var third = source.BeginReadSession())
        {
            Assert.True(third.TryGetExploredPixel(2, 2, out _));
        }

        Assert.Equal(2, source.SnapshotCloneCount);
        Assert.Equal(3, provider.GetOrLoadCalls);
    }
}

internal sealed class BudgetPixelSource(bool explored) : IExploredMapPixelSource
{
    private readonly bool _explored = explored;
    public (int X, int Z)? ExploredCoordinate { get; init; }

    public int SessionCount { get; private set; }

    public IExploredMapReadSession BeginReadSession()
    {
        SessionCount++;
        return new Session(this);
    }

    private sealed class Session(BudgetPixelSource owner) : IExploredMapReadSession
    {
        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
        {
            var found = owner._explored || owner.ExploredCoordinate == (worldX, worldZ);
            color = found ? new Rgba32(10, 20, 30, 255) : default;
            return found;
        }

        public void Dispose()
        {
        }
    }
}

internal class CountingRenderSink : ITravelMapRenderSink
{
    public virtual void TerrainCell(MapTerrainCell cell) { }
    public void ExplorationBoundary(MapBoundaryEdge edge) { }
    public void Player(Vector3 position, float heading, float size, Rgba32 color) { }
    public void Waypoint(Waypoint waypoint, Rgba32 color) { }
    public void Label(string text, Vector3 worldPosition, Rgba32 color) { }
}

internal sealed class CapturingRenderSink : CountingRenderSink
{
    public List<MapTerrainCell> Terrain { get; } = [];

    public override void TerrainCell(MapTerrainCell cell) => Terrain.Add(cell);
}

internal sealed class CountingTileProvider(MapTile tile) : IMapTileProvider
{
    public int GetOrLoadCalls { get; private set; }

    public MapTile GetOrLoad(int tileX, int tileZ)
    {
        GetOrLoadCalls++;
        return tile;
    }
}
