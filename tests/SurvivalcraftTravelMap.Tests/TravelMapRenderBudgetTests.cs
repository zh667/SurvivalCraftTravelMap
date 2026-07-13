using System.Collections.Concurrent;
using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;
using SurvivalcraftTravelMap.Waypoints;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapRenderBudgetTests
{
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
    public async Task Snapshot_version_and_rgba_are_coherent_under_concurrent_writes()
    {
        var tile = new MapTile(0, 0);
        var colors = new[]
        {
            new Rgba32(1, 2, 3, 4),
            new Rgba32(101, 102, 103, 104),
        };
        var failures = new ConcurrentQueue<Rgba32>();
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 50_000; i++)
            {
                tile.SetPixel(1, 1, colors[i & 1]);
            }
        }, TestContext.Current.CancellationToken);

        start.Set();
        while (!writer.IsCompleted)
        {
            var captured = tile.CreateVersionedSnapshot();
            if (captured.Snapshot.TryGetPixel(1, 1, out var color) && !colors.Contains(color))
            {
                failures.Enqueue(color);
            }
        }

        await writer;
        Assert.Empty(failures);
        Assert.Equal(50_000, tile.Version);
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
