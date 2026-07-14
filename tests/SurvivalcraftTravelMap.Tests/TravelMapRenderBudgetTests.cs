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
    private static MapTile CreateQuarterExploredTile(Rgba32 color)
    {
        var tile = new MapTile(0, 0);
        for (var z = 0; z < MapTile.Size; z += 2)
        {
            for (var x = 0; x < MapTile.Size; x += 2)
            {
                tile.SetPixel(x, z, color);
            }
        }

        return tile;
    }

    private static (int X, int Z, Vector2 Minimum, Vector2 Maximum, Rgba32 Color) CellIdentity(
        MapTerrainCell cell) =>
        (cell.WorldX, cell.WorldZ, cell.ScreenMinimum, cell.ScreenMaximum, cell.Color);

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
        var colors = Enumerable.Repeat(
            new Rgba32(10, 20, 30, 255),
            MapTile.Size * MapTile.Size).ToArray();
        for (var tileZ = -7; tileZ < 8; tileZ++)
        {
            for (var tileX = -10; tileX < 10; tileX++)
            {
                using var mutation = seed.AcquireMutation(tileX, tileZ);
                mutation.Tile.SetRegion(0, 0, MapTile.Size, MapTile.Size, colors);
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
        var secondSink = new CapturingRenderSink();
        var secondStatistics = TravelMapRenderModel.RenderTerrain(source, transform, 1f, secondSink);
        var secondFrameDiagnostics = store.Diagnostics;
        var secondFrameClones = source.SnapshotCloneCount;
        var secondFrameCachedLodSamples = source.CachedLodSampleCount;
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
        Assert.Equal(300, firstFrameDiagnostics.TileMaterializations);
        Assert.Equal(
            firstFrameDiagnostics.TileMaterializations,
            firstFrameDiagnostics.FileProbeCount);
        Assert.Equal(
            firstFrameDiagnostics.TileMaterializations,
            firstFrameDiagnostics.DiskReadAttempts);
        Assert.Equal(300, firstFrameClones);
        Assert.NotEmpty(secondSink.Terrain);
        Assert.InRange(
            secondStatistics.PixelQueries,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            secondStatistics.PrimitiveCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.Equal(
            300,
            secondSink.Terrain
                .Select(cell => TileCoordinate.FromWorld(cell.WorldX, cell.WorldZ))
                .Select(coordinate => (coordinate.TileX, coordinate.TileZ))
                .Distinct()
                .Count());
        Assert.Equal(300, secondFrameDiagnostics.TileMaterializations);
        Assert.Equal(300, secondFrameDiagnostics.FileProbeCount);
        Assert.Equal(300, secondFrameDiagnostics.DiskReadAttempts);
        Assert.Equal(300, secondFrameClones);
        Assert.Equal(secondFrameDiagnostics.TileMaterializations, store.Diagnostics.TileMaterializations);
        Assert.Equal(secondFrameDiagnostics.FileProbeCount, store.Diagnostics.FileProbeCount);
        Assert.Equal(secondFrameDiagnostics.DiskReadAttempts, store.Diagnostics.DiskReadAttempts);
        Assert.Equal(secondFrameClones, source.SnapshotCloneCount);
        Assert.InRange(store.Diagnostics.CachedTileCount, 1, store.Capacity);
        Assert.InRange(
            secondFrameCachedLodSamples,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.Equal(secondFrameCachedLodSamples, source.CachedLodSampleCount);
    }

    [Fact]
    public async Task Production_store_lod_covers_all_129_contiguous_tiles_with_negative_unaligned_edges()
    {
        const int firstTileX = -64;
        const int lastTileX = 64;
        const int tileZ = -2;
        const int tileCount = lastTileX - firstTileX + 1;
        const int visibleStartX = (firstTileX * MapTile.Size) + 1;
        const int visibleEndX = ((lastTileX + 1) * MapTile.Size) - 2;
        const int visibleStartZ = (tileZ * MapTile.Size) + 1;
        const int visibleEndZ = ((tileZ + 1) * MapTile.Size) - 2;
        using var directory = new TemporaryDirectory();
        var seed = new ExplorationTileStore(directory.Path, capacity: tileCount);
        var colors = Enumerable.Repeat(
            new Rgba32(20, 40, 60, 255),
            MapTile.Size * MapTile.Size).ToArray();
        for (var tileX = firstTileX; tileX <= lastTileX; tileX++)
        {
            using var mutation = seed.AcquireMutation(tileX, tileZ);
            mutation.Tile.SetRegion(0, 0, MapTile.Size, MapTile.Size, colors);
        }

        await seed.FlushAsync(TestContext.Current.CancellationToken);
        var store = new ExplorationTileStore(directory.Path, capacity: 128);
        var source = new TileStoreMapPixelSource(store);
        var sink = new RowCapturingRenderSink(visibleStartZ);
        var transform = new MapTransform(
            new Vector2(
                (visibleStartX + visibleEndX) / 2f,
                (visibleStartZ + visibleEndZ) / 2f),
            1f,
            new Vector2(
                visibleEndX - visibleStartX,
                visibleEndZ - visibleStartZ));

        var statistics = TravelMapRenderModel.RenderTerrain(source, transform, 1f, sink);
        var diagnostics = store.Diagnostics;

        Assert.Equal(2, statistics.WorldStride);
        Assert.Equal(tileCount, diagnostics.TileMaterializations);
        Assert.Equal(tileCount, diagnostics.FileProbeCount);
        Assert.Equal(tileCount, diagnostics.DiskReadAttempts);
        Assert.Equal(tileCount, source.SnapshotCloneCount);
        Assert.InRange(diagnostics.CachedTileCount, 1, store.Capacity);
        Assert.InRange(
            source.CachedLodSampleCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            statistics.PixelQueries,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            statistics.PrimitiveCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);

        Assert.NotEmpty(sink.Terrain);
        Assert.Equal(
            transform.WorldToScreen(new Vector2(visibleStartX, visibleStartZ)).X,
            sink.Terrain[0].ScreenMinimum.X);
        Assert.Equal(
            transform.WorldToScreen(new Vector2(visibleEndX + 1, visibleStartZ)).X,
            sink.Terrain[^1].ScreenMaximum.X);
        for (var index = 1; index < sink.Terrain.Count; index++)
        {
            Assert.Equal(sink.Terrain[index - 1].ScreenMaximum.X, sink.Terrain[index].ScreenMinimum.X);
        }
    }

    [Fact]
    public void Fixed_scale_pan_round_trip_retains_partially_explored_lod_cells()
    {
        const int tileCount = 129;
        var color = new Rgba32(70, 80, 90, 255);
        var source = new TileStoreMapPixelSource(
            new KnownTileProvider(CreateQuarterExploredTile(color), tileCount));
        var viewport = new Vector2((tileCount * MapTile.Size) - 2, MapTile.Size - 2);
        var original = new MapTransform(
            new Vector2(((tileCount * MapTile.Size) - 1) / 2f, (MapTile.Size - 1) / 2f),
            1f,
            viewport);
        var shifted = original with { Center = original.Center + new Vector2(1f, 1f) };

        var first = new CapturingRenderSink();
        var middle = new CapturingRenderSink();
        var returned = new CapturingRenderSink();
        var firstStats = TravelMapRenderModel.RenderTerrain(source, original, 1f, first);
        TravelMapRenderModel.RenderTerrain(source, shifted, 1f, middle);
        var returnedStats = TravelMapRenderModel.RenderTerrain(source, original, 1f, returned);

        Assert.True(firstStats.WorldStride > 1);
        Assert.NotEmpty(first.Terrain);
        Assert.NotEmpty(middle.Terrain);
        Assert.Equal(firstStats.WorldStride, returnedStats.WorldStride);
        Assert.Equal(
            first.Terrain.Select(CellIdentity).ToArray(),
            returned.Terrain.Select(CellIdentity).ToArray());
        Assert.All(returned.Terrain, cell => Assert.Equal(color, cell.Color));
    }

    [Fact]
    public void Lod_cache_progressively_completes_513_tiles_with_bounded_builds_and_no_permanent_gaps()
    {
        const int tileCount = TileStoreMapPixelSource.MaximumLodTileMaterializationsPerFrame + 1;
        var tile = CreateFullyExploredTile(new Rgba32(20, 40, 60, 255));
        var provider = new KnownTileProvider(tile, tileCount);
        var source = new TileStoreMapPixelSource(provider);
        var transform = new MapTransform(
            new Vector2(((tileCount * MapTile.Size) - 1) / 2f, (MapTile.Size - 1) / 2f),
            1f,
            new Vector2((tileCount * MapTile.Size) - 1, MapTile.Size - 1));

        var firstStatistics = TravelMapRenderModel.RenderTerrain(
            source,
            transform,
            1f,
            new CountingRenderSink());
        var firstFrameCalls = provider.GetOrLoadCalls;
        var secondSink = new CapturingRenderSink();
        var secondStatistics = TravelMapRenderModel.RenderTerrain(source, transform, 1f, secondSink);
        var secondFrameCalls = provider.GetOrLoadCalls;
        TravelMapRenderModel.RenderTerrain(source, transform, 1f, new CountingRenderSink());

        Assert.Equal(TileStoreMapPixelSource.MaximumLodTileMaterializationsPerFrame, firstFrameCalls);
        Assert.Equal(tileCount, secondFrameCalls);
        Assert.Equal(secondFrameCalls, provider.GetOrLoadCalls);
        Assert.Equal(tileCount, source.SnapshotCloneCount);
        Assert.InRange(
            source.CachedLodSampleCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            firstStatistics.PixelQueries,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            secondStatistics.PixelQueries,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            secondStatistics.PrimitiveCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.Equal(
            tileCount,
            secondSink.Terrain
                .Select(cell => TileCoordinate.FromWorld(cell.WorldX, cell.WorldZ).TileX)
                .Distinct()
                .Count());
    }

    [Fact]
    public void Catalog_larger_than_the_one_command_per_tile_limit_terminates_without_materialization()
    {
        const int tileCount = TravelMapRenderModel.MaximumTerrainSamplesPerFrame + 1;
        var provider = new KnownTileProvider(CreateFullyExploredTile(new Rgba32(20, 40, 60, 255)), tileCount);
        var source = new TileStoreMapPixelSource(provider);

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 32f, new Vector2(1920f, 1080f)),
            1f,
            new CountingRenderSink());

        Assert.Equal(default, statistics);
        Assert.InRange(
            provider.CatalogCoordinatesReturned,
            0,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.Equal(0, provider.GetOrLoadCalls);
        Assert.Equal(0, source.SnapshotCloneCount);
        Assert.Equal(0, source.CachedLodSampleCount);
    }

    [Fact]
    public void Production_lod_cache_invalidates_after_a_tile_mutation()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path, capacity: 128);
        var firstColor = new Rgba32(20, 40, 60, 255);
        var secondColor = new Rgba32(80, 100, 120, 255);
        var firstColors = Enumerable.Repeat(firstColor, MapTile.Size * MapTile.Size).ToArray();
        var secondColors = Enumerable.Repeat(secondColor, MapTile.Size * MapTile.Size).ToArray();
        using (var mutation = store.AcquireMutation(0, 0))
        {
            mutation.Tile.SetRegion(0, 0, MapTile.Size, MapTile.Size, firstColors);
        }

        var source = new TileStoreMapPixelSource(store);
        var transform = new MapTransform(
            new Vector2((MapTile.Size - 1) / 2f),
            1f,
            new Vector2(MapTile.Size - 1));
        var firstSink = new CapturingRenderSink();
        TravelMapRenderModel.RenderTerrain(source, transform, 1f, firstSink);
        using (var mutation = store.AcquireMutation(0, 0))
        {
            mutation.Tile.SetRegion(0, 0, MapTile.Size, MapTile.Size, secondColors);
        }

        var secondSink = new CapturingRenderSink();
        TravelMapRenderModel.RenderTerrain(source, transform, 1f, secondSink);

        Assert.NotEmpty(firstSink.Terrain);
        Assert.All(firstSink.Terrain, cell => Assert.Equal(firstColor, cell.Color));
        Assert.NotEmpty(secondSink.Terrain);
        Assert.All(secondSink.Terrain, cell => Assert.Equal(secondColor, cell.Color));
        Assert.Equal(2, source.SnapshotCloneCount);
        Assert.InRange(
            source.CachedLodSampleCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
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

    [Fact]
    public void Indexed_stride_two_renders_a_fully_explored_tile_row_without_periodic_gaps()
    {
        var tile = new MapTile(0, 0);
        tile.SetRegion(
            0,
            0,
            MapTile.Size,
            MapTile.Size,
            Enumerable.Repeat(new Rgba32(20, 40, 60, 255), MapTile.Size * MapTile.Size).ToArray());
        var source = new TileStoreMapPixelSource(new KnownTileProvider(tile, tileCount: 65));
        var sink = new CapturingRenderSink();

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(2079.5f, 31.5f), 1f, new Vector2(4160f, 64f)),
            1f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        var firstTileRow = sink.Terrain
            .Where(cell => cell.WorldZ == 0 && cell.WorldX is >= 0 and < MapTile.Size)
            .OrderBy(cell => cell.WorldX)
            .ToArray();
        Assert.Equal(MapTile.Size / 2, firstTileRow.Length);
        Assert.All(firstTileRow, cell =>
        {
            Assert.Equal(2f, cell.ScreenMaximum.X - cell.ScreenMinimum.X);
            Assert.Equal(2f, cell.ScreenMaximum.Y - cell.ScreenMinimum.Y);
        });
        for (var index = 1; index < firstTileRow.Length; index++)
        {
            Assert.Equal(firstTileRow[index - 1].ScreenMaximum.X, firstTileRow[index].ScreenMinimum.X);
        }
    }

    [Fact]
    public void Indexed_aggregate_retains_known_color_when_some_cells_in_the_region_are_unknown()
    {
        var color = new Rgba32(20, 40, 60, 255);
        var tile = CreateFullyExploredTile(color);
        tile.SetPixel(1, 0, default);
        var source = new TileStoreMapPixelSource(new KnownTileProvider(tile, tileCount: 65));
        var sink = new CapturingRenderSink();

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(2079.5f, 31.5f), 1f, new Vector2(4160f, 64f)),
            1f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        var partiallyExploredCell = Assert.Single(
            sink.Terrain,
            cell => cell.WorldX == 0 && cell.WorldZ == 0);
        Assert.Equal(color, partiallyExploredCell.Color);
        Assert.Equal(2f, partiallyExploredCell.ScreenMaximum.X - partiallyExploredCell.ScreenMinimum.X);
        var neighboringCell = Assert.Single(
            sink.Terrain,
            cell => cell.WorldX == 2 && cell.WorldZ == 0);
        Assert.Equal(2f, neighboringCell.ScreenMaximum.X - neighboringCell.ScreenMinimum.X);
        Assert.InRange(
            statistics.PixelQueries,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
        Assert.InRange(
            source.CachedLodSampleCount,
            1,
            TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
    }

    [Fact]
    public void Indexed_partial_tile_edge_aggregate_covers_only_the_visible_cell()
    {
        var tile = CreateFullyExploredTile(new Rgba32(20, 40, 60, 255));
        var source = new TileStoreMapPixelSource(new KnownTileProvider(tile, tileCount: 65));
        var sink = new CapturingRenderSink();

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(63f, 63f), 1f, Vector2.One),
            1f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        var cell = Assert.Single(sink.Terrain);
        Assert.Equal((63, 63), (cell.WorldX, cell.WorldZ));
        Assert.Equal(1f, cell.ScreenMaximum.X - cell.ScreenMinimum.X);
        Assert.Equal(1f, cell.ScreenMaximum.Y - cell.ScreenMinimum.Y);
    }

    [Fact]
    public void Generic_aggregate_capability_renders_stride_two_continuously_and_tints_after_aggregation()
    {
        var source = new AggregateBudgetPixelSource(new Rgba32(101, 81, 61, 255));
        var sink = new RowCapturingRenderSink(rowZ: 0);

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(Vector2.Zero, 1f, new Vector2(1024f, 513f)),
            0.5f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        Assert.Equal(0, source.PixelQueries);
        Assert.NotEmpty(sink.Terrain);
        Assert.All(sink.Terrain, cell => Assert.Equal(new Rgba32(50, 40, 30, 255), cell.Color));
        for (var index = 1; index < sink.Terrain.Count; index++)
        {
            Assert.Equal(sink.Terrain[index - 1].ScreenMaximum.X, sink.Terrain[index].ScreenMinimum.X);
        }
    }

    [Fact]
    public void Generic_stride_two_covers_every_cell_in_an_unaligned_visible_range()
    {
        var source = new AggregateBudgetPixelSource(new Rgba32(20, 40, 60, 255));
        var sink = new RowCapturingRenderSink(rowZ: 0);
        var transform = new MapTransform(
            new Vector2(1f, 1f),
            1f,
            new Vector2(1024f, 513f));

        var statistics = TravelMapRenderModel.RenderTerrain(source, transform, 1f, sink);

        Assert.Equal(2, statistics.WorldStride);
        Assert.NotEmpty(sink.Terrain);
        Assert.Equal(-511, sink.Terrain[0].WorldX);
        Assert.Equal(
            transform.WorldToScreen(new Vector2(-511f, 0f)).X,
            sink.Terrain[0].ScreenMinimum.X);
        Assert.Equal(
            transform.WorldToScreen(new Vector2(514f, 0f)).X,
            sink.Terrain[^1].ScreenMaximum.X);
        Assert.Equal(1f, sink.Terrain[0].ScreenMaximum.X - sink.Terrain[0].ScreenMinimum.X);
        Assert.Equal(2f, sink.Terrain[^1].ScreenMaximum.X - sink.Terrain[^1].ScreenMinimum.X);
        for (var index = 1; index < sink.Terrain.Count; index++)
        {
            Assert.Equal(sink.Terrain[index - 1].ScreenMaximum.X, sink.Terrain[index].ScreenMinimum.X);
        }
    }

    [Fact]
    public void Generic_partial_aggregates_ignore_unknown_cells_outside_the_visible_range()
    {
        var source = new AggregateBudgetPixelSource(
            new Rgba32(20, 40, 60, 255),
            (x, z) => x is >= -511 and <= 512 && z is >= -255 and <= 256);
        var sink = new RowCapturingRenderSink(rowZ: -255);

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(0.5f), 1f, new Vector2(1023f, 511f)),
            1f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        var first = Assert.Single(sink.Terrain, cell => cell.WorldX == -511);
        var last = Assert.Single(sink.Terrain, cell => cell.WorldX == 512);
        Assert.Equal(1f, first.ScreenMaximum.X - first.ScreenMinimum.X);
        Assert.Equal(1f, last.ScreenMaximum.X - last.ScreenMinimum.X);
        Assert.All(source.AggregateQueries, query =>
        {
            Assert.True(query.WorldX >= -511, $"Query began outside the viewport at {query.WorldX}.");
            Assert.True(
                (long)query.WorldX + query.Width - 1 <= 512,
                $"Query ended outside the viewport at {(long)query.WorldX + query.Width - 1}.");
            Assert.True(query.WorldZ >= -255, $"Query began outside the viewport at Z={query.WorldZ}.");
            Assert.True(
                (long)query.WorldZ + query.Height - 1 <= 256,
                $"Query ended outside the viewport at Z={(long)query.WorldZ + query.Height - 1}.");
        });
    }

    [Theory]
    [InlineData(-511, 512)]
    [InlineData(512, -511)]
    public void Generic_partial_aggregate_does_not_expand_an_unknown_visible_edge_cell(
        int unknownX,
        int otherEdgeX)
    {
        var source = new AggregateBudgetPixelSource(
            new Rgba32(20, 40, 60, 255),
            (x, _) => x != unknownX);
        var sink = new RowCapturingRenderSink(rowZ: 0);

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(0.5f, 0f), 1f, new Vector2(1023f, 513f)),
            1f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        Assert.DoesNotContain(sink.Terrain, cell => cell.WorldX == unknownX);
        Assert.Contains(sink.Terrain, cell => cell.WorldX == otherEdgeX);
    }

    [Fact]
    public void Generic_stride_two_without_aggregate_capability_samples_the_clipped_visible_edge()
    {
        var source = new BudgetPixelSource(explored: false) { ExploredCoordinate = (-511, 0) };
        var sink = new RowCapturingRenderSink(rowZ: 0);

        var statistics = TravelMapRenderModel.RenderTerrain(
            source,
            new MapTransform(new Vector2(1f, 0f), 1f, new Vector2(1024f, 513f)),
            1f,
            sink);

        Assert.Equal(2, statistics.WorldStride);
        var cell = Assert.Single(sink.Terrain);
        Assert.Equal((-511, 0), (cell.WorldX, cell.WorldZ));
        Assert.Equal(1f, cell.ScreenMaximum.X - cell.ScreenMinimum.X);
        Assert.Equal(1f, cell.ScreenMaximum.Y - cell.ScreenMinimum.Y);
    }

    [Fact]
    public void Sparse_recorded_tiles_remain_visible_through_minimap_scale_and_pan_round_trips()
    {
        var knownTiles = new[]
        {
            new MapTileCoordinate(-3, -2),
            new MapTileCoordinate(-1, 1),
            new MapTileCoordinate(0, 0),
            new MapTileCoordinate(1, -1),
            new MapTileCoordinate(3, 2),
        };
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path, capacity: knownTiles.Length);
        var colors = Enumerable.Repeat(
            new Rgba32(20, 40, 60, 255),
            MapTile.Size * MapTile.Size).ToArray();
        foreach (var tile in knownTiles)
        {
            using var mutation = store.AcquireMutation(tile.X, tile.Z);
            mutation.Tile.SetRegion(0, 0, MapTile.Size, MapTile.Size, colors);
        }

        var source = new TileStoreMapPixelSource(store);
        var controller = new TravelMapUiController();
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(192f));
        var scales = new[] { 2f, 0.5f, 0.35355335f, 0.25f, 1f, 8f, 2f };
        var panDeltas = new[]
        {
            new Vector2(3f, -2f),
            new Vector2(-3f, 2f),
        };

        foreach (var scale in scales)
        {
            transform = transform with { BlocksPerPixel = scale };
            AssertVisibleKnownPixelsAreRendered();
            foreach (var panDelta in panDeltas)
            {
                var pan = controller.HandlePan(transform, panDelta, isDragging: true);
                transform = Assert.IsType<MapTransform>(pan.Transform);
                AssertVisibleKnownPixelsAreRendered();
            }
        }

        void AssertVisibleKnownPixelsAreRendered()
        {
            var sink = new CapturingRenderSink();
            var statistics = TravelMapRenderModel.RenderTerrain(source, transform, 1f, sink);

            Assert.InRange(
                statistics.PixelQueries,
                0,
                TravelMapRenderModel.MaximumTerrainSamplesPerFrame);
            Assert.All(sink.Terrain, cell =>
            {
                Assert.True(float.IsFinite(cell.ScreenMinimum.X));
                Assert.True(float.IsFinite(cell.ScreenMinimum.Y));
                Assert.True(float.IsFinite(cell.ScreenMaximum.X));
                Assert.True(float.IsFinite(cell.ScreenMaximum.Y));
            });
            var visibleKnownPixels = knownTiles
                .Select(tile => new Vector2(tile.X * MapTile.Size, tile.Z * MapTile.Size))
                .Where(knownPixel =>
                {
                    var screen = transform.WorldToScreen(knownPixel);
                    return screen.X >= 0f
                        && screen.X <= transform.ViewportSize.X
                        && screen.Y >= 0f
                        && screen.Y <= transform.ViewportSize.Y;
                })
                .ToArray();
            Assert.NotEmpty(visibleKnownPixels);
            foreach (var knownPixel in visibleKnownPixels)
            {
                Assert.Contains(
                    sink.Terrain,
                    cell => cell.WorldX == knownPixel.X && cell.WorldZ == knownPixel.Y);
            }
        }
    }

    private static MapTile CreateFullyExploredTile(Rgba32 color)
    {
        var tile = new MapTile(0, 0);
        tile.SetRegion(
            0,
            0,
            MapTile.Size,
            MapTile.Size,
            Enumerable.Repeat(color, MapTile.Size * MapTile.Size).ToArray());
        return tile;
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
    public void Explored_region_returns_the_rounded_integer_average_of_all_rgba_channels()
    {
        var tile = new MapTile(0, 0);
        tile.SetRegion(
            2,
            3,
            2,
            2,
            [
                new Rgba32(0, 10, 20, 40),
                new Rgba32(10, 20, 30, 60),
                new Rgba32(20, 30, 40, 80),
                new Rgba32(31, 41, 51, 101),
            ]);

        var found = tile.CreateVersionedSnapshot().Snapshot.TryGetExploredRegion(
            2,
            3,
            2,
            2,
            out var average);

        Assert.True(found);
        Assert.Equal(new Rgba32(15, 25, 35, 70), average);
    }

    [Fact]
    public void Partially_explored_region_averages_only_known_rgba_values()
    {
        var tile = new MapTile(0, 0);
        tile.SetPixel(0, 0, new Rgba32(10, 20, 30, 40));
        tile.SetPixel(1, 1, new Rgba32(30, 40, 50, 80));

        var found = tile.CreateVersionedSnapshot().Snapshot.TryGetExploredRegion(
            0,
            0,
            2,
            2,
            out var average);

        Assert.True(found);
        Assert.Equal(new Rgba32(20, 30, 40, 60), average);
    }

    [Fact]
    public void Completely_unknown_region_remains_absent()
    {
        var snapshot = new MapTile(0, 0).CreateVersionedSnapshot().Snapshot;

        Assert.False(snapshot.TryGetExploredRegion(0, 0, 2, 2, out _));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(63, 0, 2, 1)]
    [InlineData(0, 63, 1, 2)]
    public void Explored_region_rejects_invalid_bounds(int x, int z, int width, int height)
    {
        var snapshot = new MapTile(0, 0).CreateVersionedSnapshot().Snapshot;

        Assert.ThrowsAny<ArgumentOutOfRangeException>(
            () => snapshot.TryGetExploredRegion(x, z, width, height, out _));
    }

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
        const int writeCount = 10_000;
        const int maximumSnapshotAttempts = 100_000;
        var coordinationTimeout = TimeSpan.FromSeconds(30);
        var tile = new MapTile(0, 0);
        var oldColor = new Rgba32(1, 2, 3, 255);
        var newColor = new Rgba32(101, 102, 103, 255);
        var regions = new[]
        {
            Enumerable.Repeat(newColor, width * height).ToArray(),
            Enumerable.Repeat(oldColor, width * height).ToArray(),
        };
        tile.SetRegion(originX, originZ, width, height, regions[1]);
        var initialVersion = tile.Version;
        var finalVersion = initialVersion + writeCount;
        var failures = new ConcurrentQueue<string>();
        var checkedSnapshotCount = 0;
        var intermediateSnapshotCount = 0;
        using var participantsReady = new CountdownEvent(2);
        using var releaseTogether = new ManualResetEventSlim();
        using var readerEnteredLoop = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerFinished = new ManualResetEventSlim();
        using var firstIntermediateSnapshotObserved = new ManualResetEventSlim();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeoutCancellation.CancelAfter(coordinationTimeout);
        var cancellationToken = timeoutCancellation.Token;

        var reader = Task.Run(() =>
        {
            participantsReady.Signal();
            releaseTogether.Wait(cancellationToken);

            CheckSnapshot(tile.CreateVersionedSnapshot());
            readerEnteredLoop.Set();
            if (!writerStarted.Wait(coordinationTimeout, cancellationToken))
            {
                throw new TimeoutException("Writer did not start after the reader entered its snapshot loop.");
            }

            while (!writerFinished.IsSet && checkedSnapshotCount < maximumSnapshotAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captured = tile.CreateVersionedSnapshot();
                CheckSnapshot(captured);
                checkedSnapshotCount++;
                if (captured.Version > initialVersion
                    && captured.Version < finalVersion
                    && !writerFinished.IsSet)
                {
                    intermediateSnapshotCount++;
                    firstIntermediateSnapshotObserved.Set();
                }

                Thread.Yield();
            }

            if (!writerFinished.IsSet)
            {
                throw new TimeoutException(
                    $"Reader exhausted {maximumSnapshotAttempts} bounded attempts before the writer finished.");
            }

            CheckSnapshot(tile.CreateVersionedSnapshot());
        }, cancellationToken);

        var writer = Task.Run(() =>
        {
            participantsReady.Signal();
            releaseTogether.Wait(cancellationToken);
            if (!readerEnteredLoop.Wait(coordinationTimeout, cancellationToken))
            {
                throw new TimeoutException("Reader did not enter its snapshot loop before the writer start gate.");
            }

            writerStarted.Set();
            try
            {
                for (var i = 0; i < writeCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tile.SetRegion(originX, originZ, width, height, regions[i & 1]);
                    if (i == 0
                        && !firstIntermediateSnapshotObserved.Wait(
                            coordinationTimeout,
                            cancellationToken))
                    {
                        throw new TimeoutException(
                            "Reader did not capture an intermediate snapshot after the first region commit.");
                    }

                    Thread.Yield();
                }
            }
            finally
            {
                writerFinished.Set();
            }
        }, cancellationToken);

        try
        {
            if (!participantsReady.Wait(coordinationTimeout, cancellationToken))
            {
                throw new TimeoutException("Reader and writer did not both reach the coordinated start barrier.");
            }

            releaseTogether.Set();
            await Task.WhenAll(reader, writer).WaitAsync(
                coordinationTimeout,
                TestContext.Current.CancellationToken);
            Assert.Empty(failures);
            Assert.True(checkedSnapshotCount > 0, "Reader did not inspect any snapshot after writer start.");
            Assert.True(
                intermediateSnapshotCount > 0,
                "Reader did not inspect a snapshot with an intermediate version while the writer was active.");
            Assert.Equal(finalVersion, tile.Version);
        }
        finally
        {
            timeoutCancellation.Cancel();
            releaseTogether.Set();
            readerEnteredLoop.Set();
            writerStarted.Set();
            writerFinished.Set();
            firstIntermediateSnapshotObserved.Set();
            try
            {
                await Task.WhenAll(reader, writer);
            }
            catch
            {
                _ = reader.Exception;
                _ = writer.Exception;
            }
        }

        void CheckSnapshot(VersionedMapTileSnapshot captured)
        {
            if (captured.Version < initialVersion || captured.Version > finalVersion)
            {
                failures.Enqueue(
                    $"Snapshot version {captured.Version} was outside [{initialVersion}, {finalVersion}].");
                return;
            }

            var expected = ((captured.Version - initialVersion) & 1) == 0
                ? oldColor
                : newColor;
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
                        failures.Enqueue(
                            $"Snapshot {captured.Version} had {actual} at "
                            + $"({originX + localX}, {originZ + localZ}); expected {expected}.");
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

internal sealed class RowCapturingRenderSink(int rowZ) : CountingRenderSink
{
    public List<MapTerrainCell> Terrain { get; } = [];

    public override void TerrainCell(MapTerrainCell cell)
    {
        if (cell.WorldZ == rowZ)
        {
            Terrain.Add(cell);
        }
    }
}

internal readonly record struct AggregateQuery(int WorldX, int WorldZ, int Width, int Height);

internal sealed class AggregateBudgetPixelSource(
    Rgba32 aggregateColor,
    Func<int, int, bool>? explored = null) : IExploredMapPixelSource
{
    public int PixelQueries { get; private set; }

    public List<AggregateQuery> AggregateQueries { get; } = [];

    public IExploredMapReadSession BeginReadSession() => new Session(this, aggregateColor, explored);

    private sealed class Session(
        AggregateBudgetPixelSource owner,
        Rgba32 aggregateColor,
        Func<int, int, bool>? explored) :
        IExploredMapReadSession,
        IExploredMapAggregateReadSession
    {
        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
        {
            owner.PixelQueries++;
            var found = explored?.Invoke(worldX, worldZ) ?? true;
            color = found ? aggregateColor : default;
            return found;
        }

        public bool TryGetExploredRegion(
            int worldX,
            int worldZ,
            int width,
            int height,
            out Rgba32 color)
        {
            owner.AggregateQueries.Add(new AggregateQuery(worldX, worldZ, width, height));
            for (var localZ = 0; localZ < height; localZ++)
            {
                var z = checked(worldZ + localZ);
                for (var localX = 0; localX < width; localX++)
                {
                    var x = checked(worldX + localX);
                    if (explored?.Invoke(x, z) is false)
                    {
                        color = default;
                        return false;
                    }
                }
            }

            color = aggregateColor;
            return true;
        }

        public void Dispose()
        {
        }
    }
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

internal sealed class KnownTileProvider(MapTile tile, int tileCount) : IMapTileProvider
{
    public int GetOrLoadCalls { get; private set; }

    public int CatalogCoordinatesReturned { get; private set; }

    public MapTile GetOrLoad(int tileX, int tileZ)
    {
        GetOrLoadCalls++;
        return tile;
    }

    public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region)
    {
        CatalogCoordinatesReturned = tileCount;
        return Enumerable.Range(0, tileCount)
            .Select(tileX => new MapTileCoordinate(tileX, 0))
            .ToArray();
    }

    public MapTileCatalog GetKnownTileCatalog(MapTileRegion region, int maximumCount)
    {
        CatalogCoordinatesReturned = Math.Min(tileCount, maximumCount);
        return new MapTileCatalog(
            Enumerable.Range(0, CatalogCoordinatesReturned)
                .Select(tileX => new MapTileCoordinate(tileX, 0))
                .ToArray(),
            IsTruncated: tileCount > maximumCount);
    }
}
