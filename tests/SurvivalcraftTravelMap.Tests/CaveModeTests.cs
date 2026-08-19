using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class CaveModeTests
{
    [Fact]
    public void Cave_layer_centers_are_stable_and_clamped()
    {
        Assert.Equal(1, CaveLayer.CenterForY(-200f));
        Assert.Equal(20, CaveLayer.CenterForY(20f));
        Assert.Equal(71, CaveLayer.CenterForY(71.9f));
        Assert.Equal(254, CaveLayer.CenterForY(999f));
    }

    [Fact]
    public void Cave_view_follows_player_until_manual_y_adjustment()
    {
        var state = new MapViewState();

        state.ShowCave(20f);
        Assert.Equal(MapViewMode.Cave, state.Mode);
        Assert.Equal(20, state.CaveY);
        Assert.True(state.FollowsPlayerY);

        state.StepCaveY(1);
        state.UpdatePlayerY(80f);
        Assert.Equal(21, state.CaveY);
        Assert.False(state.FollowsPlayerY);

        state.SetCaveY(12);
        Assert.Equal(12, state.CaveY);
        state.SetCaveY(999);
        Assert.Equal(CaveLayer.MaximumY, state.CaveY);

        state.FollowPlayer(80f);
        Assert.Equal(80, state.CaveY);
        Assert.True(state.FollowsPlayerY);

        state.ShowSurface();
        Assert.Equal(MapViewMode.Surface, state.Mode);
    }

    [Fact]
    public void Cave_sampler_projects_sloped_passages_within_the_selected_y_window()
    {
        var terrain = new FakeTerrainMapSource(topHeight: 90, defaultContent: 1);
        for (var x = 2; x <= 10; x++)
        {
            CarveWalkableFloor(terrain, 12 + ((x - 2) * 2), [(x, 3)]);
        }

        CarveWalkableFloor(terrain, 29, [(12, 3)]);
        var colors = new TerrainMapSampler(terrain, TerrainMapSamplerTests.CreatePixelData(
            new Dictionary<int, BlockPixelData>
            {
                [1] = new(1, new Rgba32(96, 96, 96, 255), false),
            }));
        var sampler = new CaveMapSampler(terrain, colors);
        var colorsAtY = new Rgba32[TerrainChunkCoordinate.PixelCount];
        var shades = new byte[TerrainChunkCoordinate.PixelCount];

        Assert.True(sampler.TrySampleChunk(
            new TerrainChunkCoordinate(0, 0),
            20,
            colorsAtY,
            shades));

        var lowWalkway = (3 * TerrainChunkCoordinate.Size) + 2;
        var highWalkway = (3 * TerrainChunkCoordinate.Size) + 10;
        var outsideWindow = (3 * TerrainChunkCoordinate.Size) + 12;
        var hiddenRock = (10 * TerrainChunkCoordinate.Size) + 10;
        Assert.NotEqual(CaveMapSampler.HiddenRockColor, colorsAtY[lowWalkway]);
        Assert.NotEqual(CaveMapSampler.HiddenRockColor, colorsAtY[highWalkway]);
        Assert.Equal(CaveMapSampler.HiddenRockColor, colorsAtY[outsideWindow]);
        Assert.NotEqual(shades[lowWalkway], shades[highWalkway]);
        Assert.Equal(CaveMapSampler.HiddenRockColor, colorsAtY[hiddenRock]);
    }

    [Fact]
    public void Cave_sampler_supports_the_lowest_selectable_y()
    {
        var terrain = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        CarveWalkableFloor(terrain, 1, [(4, 5)]);
        var colors = new TerrainMapSampler(terrain, TerrainMapSamplerTests.CreatePixelData());
        var sampler = new CaveMapSampler(terrain, colors);
        var colorsAtY = new Rgba32[TerrainChunkCoordinate.PixelCount];
        var shades = new byte[TerrainChunkCoordinate.PixelCount];

        Assert.True(sampler.TrySampleChunk(
            new TerrainChunkCoordinate(0, 0),
            CaveLayer.MinimumY,
            colorsAtY,
            shades));
        Assert.NotEqual(
            CaveMapSampler.HiddenRockColor,
            colorsAtY[(5 * TerrainChunkCoordinate.Size) + 4]);
    }

    [Fact]
    public async Task Cave_layer_refuses_further_tiles_once_its_backlog_fills_up()
    {
        using var directory = new TemporaryDirectory();
        var colors = Enumerable.Repeat(
            CaveMapSampler.HiddenRockColor,
            TerrainChunkCoordinate.PixelCount).ToArray();
        var shades = Enumerable.Repeat(
            TerrainHeightShading.Neutral,
            TerrainChunkCoordinate.PixelCount).ToArray();
        var store = new CaveExplorationStore(directory.Path);

        // One chunk per 64-block tile, so each recording claims a fresh cache slot.
        for (var index = 0; index < CaveExplorationStore.LayerTileCapacity; index++)
        {
            var chunk = new TerrainChunkCoordinate(index * 4, 0);
            Assert.True(store.CanAdmit(20, chunk));
            Assert.Equal(
                ExplorationRecordResult.Recorded,
                store.RecordChunk(20, chunk, colors, shades));
        }

        var overflow = new TerrainChunkCoordinate(CaveExplorationStore.LayerTileCapacity * 4, 0);
        Assert.False(store.CanAdmit(20, overflow));
        Assert.Equal(
            ExplorationRecordResult.Pressure,
            store.RecordChunk(20, overflow, colors, shades));

        // Every Y gets its own store, so a neighbouring layer still has its own budget.
        Assert.Equal(1, store.LayerCount);
        Assert.True(store.CanAdmit(21, overflow));
        Assert.Equal(2, store.LayerCount);

        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(store.CanAdmit(20, overflow));
        Assert.Equal(0, store.GetDiagnostics(20).DirtyTileCount);
    }

    [Fact]
    public async Task Cave_store_keeps_layers_separate_and_survives_reload()
    {
        using var directory = new TemporaryDirectory();
        var sampleColor = new Rgba32(70, 80, 90, 255);
        var chunk = new TerrainChunkCoordinate(0, -1);
        var colors = Enumerable.Repeat(
            CaveMapSampler.HiddenRockColor,
            TerrainChunkCoordinate.PixelCount).ToArray();
        var shades = Enumerable.Repeat(
            TerrainHeightShading.Neutral,
            TerrainChunkCoordinate.PixelCount).ToArray();
        colors[(9 * TerrainChunkCoordinate.Size) + 12] = sampleColor;
        var store = new CaveExplorationStore(directory.Path);

        Assert.Equal(
            ExplorationRecordResult.Recorded,
            store.RecordChunk(20, chunk, colors, shades));
        Assert.True(store.IsChunkFullyExplored(20, chunk));
        using (var current = store.GetPixelSource(20).BeginReadSession())
        {
            Assert.True(current.TryGetExploredPixel(12, -7, out var color));
            Assert.Equal(sampleColor, color);
        }

        using (var otherLayer = store.GetPixelSource(21).BeginReadSession())
        {
            Assert.False(otherLayer.TryGetExploredPixel(12, -7, out _));
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        var reopened = new CaveExplorationStore(directory.Path);
        using var persisted = reopened.GetPixelSource(20).BeginReadSession();
        Assert.True(persisted.TryGetExploredTerrainPixel(12, -7, out var pixel));
        Assert.Equal(sampleColor, pixel.Color);
        Assert.Equal(TerrainHeightShading.Neutral, pixel.HeightShade);
    }

    [Theory]
    [InlineData(MapShape.Square)]
    [InlineData(MapShape.RoundedSquare)]
    [InlineData(MapShape.Circle)]
    public void Offscreen_death_marker_is_clamped_to_the_correct_map_edge(MapShape shape)
    {
        var transform = new MapTransform(Vector2.Zero, 1f, new Vector2(200f));

        var projection = DeathMarkerTracking.Project(
            transform,
            new Vector2(200f),
            shape,
            new Vector2(500f, 0f),
            inset: 16f);

        // A death marker at world +X is now drawn on the left (screen -X): the 180-degree map
        // rotation makes +X the sun-west direction, which sits on the left of a north-up map.
        Assert.True(projection.IsOffscreen);
        Assert.True(projection.Position.X < 100f);
        Assert.InRange(projection.Position.Y, 99.99f, 100.01f);
        Assert.True(MapShapeGeometry.Create(new Vector2(200f), shape).ContainsPoint(projection.Position, 15.9f));
        Assert.True(projection.Direction.X < -0.99f);
    }

    [Fact]
    public void Visible_death_marker_keeps_its_true_screen_position()
    {
        var transform = new MapTransform(Vector2.Zero, 1f, new Vector2(200f));

        var projection = DeathMarkerTracking.Project(
            transform,
            new Vector2(200f),
            MapShape.Square,
            new Vector2(20f, -30f),
            inset: 16f);

        Assert.False(projection.IsOffscreen);
        Assert.Equal(transform.WorldToScreen(new Vector2(20f, -30f)), projection.Position);
    }

    [Fact]
    public void Previous_death_marker_relies_on_the_on_screen_flag_and_is_never_edge_clamped()
    {
        var transform = new MapTransform(Vector2.Zero, 1f, new Vector2(200f));

        // Off-screen: the shared projection reports IsOffscreen, which the untracked previous marker
        // uses to skip drawing entirely rather than clamp to the edge like the tracked marker.
        var offscreen = DeathMarkerTracking.Project(
            transform,
            new Vector2(200f),
            MapShape.Square,
            new Vector2(500f, 0f),
            inset: 16f);
        Assert.True(offscreen.IsOffscreen);

        // On-screen: the previous marker draws at its true position, never clamped.
        var onscreen = DeathMarkerTracking.Project(
            transform,
            new Vector2(200f),
            MapShape.Square,
            new Vector2(20f, -30f),
            inset: 16f);
        Assert.False(onscreen.IsOffscreen);
        Assert.Equal(transform.WorldToScreen(new Vector2(20f, -30f)), onscreen.Position);
    }

    private static void CarveWalkableFloor(
        FakeTerrainMapSource terrain,
        int feetY,
        IEnumerable<(int X, int Z)> cells)
    {
        foreach (var (x, z) in cells)
        {
            terrain.SetContent(x, feetY - 1, z, 1);
            terrain.SetContent(x, feetY, z, 0);
            terrain.SetContent(x, feetY + 1, z, 0);
        }
    }
}

public sealed class MapLayerIdentityTests
{
    [Fact]
    public void Surface_identity_ignores_the_cave_depth()
    {
        // The cave depth follows the player's Y every frame; if it counted while the surface view
        // is on screen, walking up or down stairs would repaint the whole cached large map.
        Assert.Equal(
            MapLayerIdentity.For(MapViewMode.Surface, caveY: 3),
            MapLayerIdentity.For(MapViewMode.Surface, caveY: 9));
    }

    [Fact]
    public void Cave_identity_separates_depths_and_the_surface()
    {
        Assert.NotEqual(
            MapLayerIdentity.For(MapViewMode.Cave, caveY: 3),
            MapLayerIdentity.For(MapViewMode.Cave, caveY: 4));
        Assert.NotEqual(
            MapLayerIdentity.For(MapViewMode.Cave, caveY: 3),
            MapLayerIdentity.For(MapViewMode.Surface, caveY: 3));
    }
}
