using System.Numerics;
using Engine.Media;
using Game;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MiniMapTextRendererTests
{
    [Fact]
    public void Flat_batch_guard_flushes_before_ushort_triangle_indices_wrap()
    {
        Assert.False(MapSurfaceBatchGuard.RequiresFlush(
            triangleVertexCount: MapSurfaceBatchGuard.MaximumAddressableVertices - 4,
            additionalTriangleVertices: 4,
            lineVertexCount: 0,
            additionalLineVertices: 0));
        Assert.True(MapSurfaceBatchGuard.RequiresFlush(
            triangleVertexCount: MapSurfaceBatchGuard.MaximumAddressableVertices - 3,
            additionalTriangleVertices: 4,
            lineVertexCount: 0,
            additionalLineVertices: 0));
    }

    [Fact]
    public void Flat_batch_guard_applies_the_same_ushort_limit_to_map_lines()
    {
        Assert.False(MapSurfaceBatchGuard.RequiresFlush(
            triangleVertexCount: 0,
            additionalTriangleVertices: 0,
            lineVertexCount: MapSurfaceBatchGuard.MaximumAddressableVertices - 4,
            additionalLineVertices: 4));
        Assert.True(MapSurfaceBatchGuard.RequiresFlush(
            triangleVertexCount: 0,
            additionalTriangleVertices: 0,
            lineVertexCount: MapSurfaceBatchGuard.MaximumAddressableVertices - 3,
            additionalLineVertices: 4));
    }

    [Fact]
    public void Full_render_budget_is_partitioned_into_safe_ushort_vertex_windows()
    {
        var triangleVertices = 240;
        var flushes = 0;
        for (var primitive = 0;
             primitive < TravelMapRenderModel.MaximumTerrainSamplesPerFrame;
             primitive++)
        {
            if (MapSurfaceBatchGuard.RequiresFlush(
                    triangleVertices,
                    additionalTriangleVertices: 4,
                    lineVertexCount: 0,
                    additionalLineVertices: 0))
            {
                triangleVertices = 0;
                flushes++;
            }

            triangleVertices += 4;
            Assert.InRange(
                triangleVertices,
                0,
                MapSurfaceBatchGuard.MaximumAddressableVertices);
        }

        Assert.True(flushes > 1);
    }

    [Fact]
    public void Actual_waypoint_and_coordinate_queue_paths_pass_readable_scale()
    {
        var queue = new RecordingMapFontQueue();

        MiniMapTextRenderer.QueueWaypointLabel(
            queue,
            "home",
            Vector2.Zero,
            new Rgba32(255, 255, 255, 255));
        MiniMapTextRenderer.QueueCoordinates(
            queue,
            "X 1 / Y 2 / Z 3",
            Vector2.Zero,
            new Rgba32(255, 255, 255, 255));

        Assert.Equal(2, queue.Scales.Count);
        Assert.All(queue.Scales, scale => Assert.True(scale >= 0.8f));
    }

    [Fact]
    public void Compact_coordinate_queue_uses_the_minimap_scale_and_bottom_left_alignment()
    {
        var queue = new RecordingMapFontQueue();

        MiniMapTextRenderer.QueueCoordinates(
            queue,
            "X:1 Y:2 Z:3",
            Vector2.Zero,
            new Rgba32(255, 255, 255, 255),
            TravelMapTypography.MiniMapCoordinateScale);

        var item = Assert.Single(queue.Items);
        Assert.Equal(TravelMapTypography.MiniMapCoordinateScale, item.Scale);
        Assert.Equal(MapTextAlignment.BottomLeft, item.Alignment);
    }

    [Fact]
    public void Compass_queue_centers_text_and_uses_the_configured_scale()
    {
        var queue = new RecordingMapFontQueue();

        MiniMapTextRenderer.QueueCompassLabel(
            queue,
            "北",
            new Vector2(96f, 12f),
            TravelMapPalette.CompassNorth,
            1.5f);

        var item = Assert.Single(queue.Items);
        Assert.Equal(MapTextAlignment.Center, item.Alignment);
        Assert.Equal(1.5f, item.Scale);
    }

    [Fact]
    public void Actual_minimap_compass_draw_queues_four_centered_labels()
    {
        using var widget = CreateSurface(
            orientation: MiniMapOrientation.HeadingUp,
            heading: MathF.PI / 2f);
        widget.ApplyConfiguredMiniMapOrientation = true;
        widget.ShowCompassOverlay = true;
        var primitives = new RecordingMapSurfacePrimitiveQueue();
        var text = new RecordingMapFontQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), primitives, text));

        Assert.Equal(4, text.Items.Count);
        Assert.All(text.Items, item => Assert.Equal(MapTextAlignment.Center, item.Alignment));
    }

    [Fact]
    public void Game_time_draws_even_when_coordinates_are_hidden()
    {
        using var widget = CreateSurface(showGameTime: true);
        widget.GameTimeProvider = () => 0.5f;
        var primitives = new RecordingMapSurfacePrimitiveQueue();
        var text = new RecordingMapFontQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), primitives, text));

        var index = Assert.Single(
            Enumerable.Range(0, text.Texts.Count),
            item => text.Texts[item] == "12:00");
        Assert.Equal(MapTextAlignment.BottomRight, text.Items[index].Alignment);
    }

    [Fact]
    public void Minimap_information_uses_two_transparent_footer_lines()
    {
        using var widget = CreateSurface(showGameTime: true, showCoordinates: true);
        widget.GameTimeProvider = () => 0.5f;
        widget.PlaceMapInformationBelowSurface = true;
        widget.ShowCoordinateBackdrop = true;
        var primitives = new RecordingMapSurfacePrimitiveQueue();
        var text = new RecordingMapFontQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), primitives, text));

        Assert.DoesNotContain(
            primitives.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.CoordinateBackdrop);
        var timeIndex = text.Texts.IndexOf("12:00");
        var coordinateIndex = text.Texts.FindIndex(item => item.StartsWith("X:", StringComparison.Ordinal));
        Assert.True(timeIndex >= 0);
        Assert.True(coordinateIndex >= 0);
        Assert.Equal(210f, text.Positions[timeIndex].Y);
        Assert.Equal(230f, text.Positions[coordinateIndex].Y);
    }

    [Fact]
    public void Minimap_frame_primitives_keep_shadow_inside_192_footprint_before_two_warm_frames()
    {
        var primitives = MiniMapVisualStyle.CreateFramePrimitives(
            new Vector2(192f),
            showFrameShadow: true,
            MiniMapVisualStyle.FrameThickness,
            TravelMapPalette.MiniMapFrameShadow,
            TravelMapPalette.MiniMapFrame);

        Assert.Equal(MiniMapVisualStyle.ShadowThickness, 1f);
        Assert.Equal(MiniMapVisualStyle.FrameThickness, 2f);
        Assert.Equal(MiniMapVisualStyle.FrameShadowAlpha, (byte)0x80);
        Assert.Collection(
            primitives,
            shadow =>
            {
                Assert.Equal(MapFramePrimitiveKind.Shadow, shadow.Kind);
                Assert.Equal(new Vector2(0.5f), shadow.Minimum);
                Assert.Equal(new Vector2(191.5f), shadow.Maximum);
                Assert.Equal(new Rgba32(0x12, 0x12, 0x12, 0x80), shadow.Color);
            },
            outerFrame =>
            {
                Assert.Equal(MapFramePrimitiveKind.Frame, outerFrame.Kind);
                Assert.Equal(new Vector2(1.5f), outerFrame.Minimum);
                Assert.Equal(new Vector2(190.5f), outerFrame.Maximum);
                Assert.Equal(TravelMapPalette.MiniMapFrame, outerFrame.Color);
            },
            innerFrame =>
            {
                Assert.Equal(MapFramePrimitiveKind.Frame, innerFrame.Kind);
                Assert.Equal(new Vector2(2.5f), innerFrame.Minimum);
                Assert.Equal(new Vector2(189.5f), innerFrame.Maximum);
                Assert.Equal(TravelMapPalette.MiniMapFrame, innerFrame.Color);
            });
    }

    [Fact]
    public void Large_map_frame_plan_has_no_shadow_primitive()
    {
        var primitives = MiniMapVisualStyle.CreateFramePrimitives(
            new Vector2(640f, 480f),
            showFrameShadow: false,
            frameThickness: 1f,
            new Rgba32(0x12, 0x12, 0x12, 0x80),
            TravelMapPalette.Moss);

        var frame = Assert.Single(primitives);
        Assert.Equal(MapFramePrimitiveKind.Frame, frame.Kind);
        Assert.Equal(new Vector2(1f), frame.Minimum);
        Assert.Equal(new Vector2(639f, 479f), frame.Maximum);
    }

    [Fact]
    public void Minimap_coordinate_backdrop_is_a_translucent_lower_left_strip_no_taller_than_18()
    {
        var strip = MiniMapVisualStyle.CreateCoordinateBackdrop(new Vector2(192f));

        Assert.Equal(MiniMapVisualStyle.CoordinateStripHeight, strip.Maximum.Y - strip.Minimum.Y);
        Assert.Equal(18f, MiniMapVisualStyle.CoordinateStripHeight);
        Assert.Equal(new Vector2(0f, 174f), strip.Minimum);
        Assert.Equal(new Vector2(192f), strip.Maximum);
        Assert.True(strip.Color.A < byte.MaxValue);
    }

    [Fact]
    public void Minimap_player_primitives_draw_a_larger_dark_outline_before_the_red_marker()
    {
        var primitives = MiniMapVisualStyle.CreatePlayerPrimitives(
            Vector2.Zero,
            heading: 0f,
            size: 18f,
            TravelMapPalette.MiniMapPlayer,
            drawOutline: true);

        Assert.Collection(
            primitives,
            outline =>
            {
                Assert.Equal(TravelMapPalette.MiniMapPlayerOutline, outline.Color);
                Assert.Equal(21f, outline.Size);
            },
            marker =>
            {
                Assert.Equal(TravelMapPalette.MiniMapPlayer, marker.Color);
                Assert.Equal(18f, marker.Size);
            });
        Assert.True(primitives[0].Tip.Y < primitives[1].Tip.Y);
    }

    [Theory]
    [InlineData(CreatureMapMarkerKind.Predator, 255, 60, 60)]
    [InlineData(CreatureMapMarkerKind.Bird, 255, 220, 60)]
    [InlineData(CreatureMapMarkerKind.Other, 60, 220, 80)]
    public void Creature_marker_palette_matches_the_reference_categories(
        CreatureMapMarkerKind kind,
        byte red,
        byte green,
        byte blue)
    {
        Assert.Equal(
            new Rgba32(red, green, blue, byte.MaxValue),
            CreatureMapMarkerStyle.ColorFor(kind));
    }

    [Fact]
    public void Actual_map_draw_queues_outlined_creatures_before_the_player_marker()
    {
        using var widget = CreateSurface(
        [
            new CreatureMapMarker(new Vector3(8f, 64f, 0f), CreatureMapMarkerKind.Predator),
        ]);
        var queue = new RecordingMapSurfacePrimitiveQueue();
        widget.PlayerArrowSize = 18f;

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        var creaturePrimitives = queue.Primitives
            .Where(primitive => primitive.Kind == MapSurfacePrimitiveKind.Creature)
            .ToArray();
        Assert.Collection(
            creaturePrimitives,
            outline =>
            {
                Assert.Equal(CreatureMapMarkerStyle.OutlineColor, outline.Color);
                Assert.Equal(8f, outline.Size);
            },
            fill =>
            {
                Assert.Equal(CreatureMapMarkerStyle.PredatorColor, fill.Color);
                Assert.Equal(5f, fill.Size);
            });
        Assert.True(
            queue.Primitives.IndexOf(creaturePrimitives[1])
            < queue.Primitives.FindIndex(primitive => primitive.Kind == MapSurfacePrimitiveKind.Player));
    }

    [Fact]
    public void Creature_setting_hides_all_creature_primitives()
    {
        using var widget = CreateSurface(
        [
            new CreatureMapMarker(new Vector3(8f, 64f, 0f), CreatureMapMarkerKind.Other),
        ],
        showCreatures: false);
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        Assert.DoesNotContain(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.Creature);
    }

    [Fact]
    public void Last_death_is_drawn_as_one_skull_symbol()
    {
        var marker = new DeathMapMarker(new Vector3(8f, 22f, 0f), 3.25, "fall");
        using var widget = CreateSurface(lastDeath: marker);
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        var skull = queue.Primitives
            .Where(primitive => primitive.Kind == MapSurfacePrimitiveKind.DeathMarker)
            .ToArray();
        Assert.True(skull.Length >= 8);
        Assert.Contains(skull, primitive => primitive.Color == TravelMapPalette.DeathMarkerBone);
        Assert.Contains(skull, primitive => primitive.Color == TravelMapPalette.DeathMarkerOutline);
    }

    [Fact]
    public void Last_death_setting_hides_the_skull_and_disables_its_hit_target()
    {
        var marker = new DeathMapMarker(new Vector3(8f, 22f, 0f), 3.25, "fall");
        using var widget = CreateSurface(lastDeath: marker, showLastDeath: false);
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        Assert.DoesNotContain(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.DeathMarker);
    }

    [Fact]
    public void Offscreen_last_death_draws_on_the_edge_and_uses_the_edge_position_as_hit_target()
    {
        var marker = new DeathMapMarker(new Vector3(800f, 22f, 0f), 3.25, "fall");
        using var widget = CreateSurface(lastDeath: marker);
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));
        var projection = widget.ProjectLastDeath(marker);

        Assert.True(projection.IsOffscreen);
        Assert.NotNull(widget.HitLastDeath(projection.Position));
        Assert.Contains(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.DeathMarker);
    }

    [Theory]
    [InlineData(CreatureCategory.LandPredator, CreatureMapMarkerKind.Predator)]
    [InlineData(CreatureCategory.WaterPredator, CreatureMapMarkerKind.Predator)]
    [InlineData(CreatureCategory.Bird, CreatureMapMarkerKind.Bird)]
    [InlineData(CreatureCategory.LandOther, CreatureMapMarkerKind.Other)]
    [InlineData(CreatureCategory.WaterOther, CreatureMapMarkerKind.Other)]
    public void Game_creature_categories_map_to_stable_marker_kinds(
        CreatureCategory category,
        CreatureMapMarkerKind expected)
    {
        Assert.Equal(expected, TravelMapComponent.ToCreatureMarkerKind(category));
    }

    [Fact]
    public void Native_player_death_history_selects_only_the_latest_record_for_the_map()
    {
        var stats = new PlayerStats();
        stats.AddDeathRecord(new PlayerStats.DeathRecord
        {
            Day = 1.5,
            Location = new Engine.Vector3(10f, 20f, 30f),
            Cause = "old",
        });
        stats.AddDeathRecord(new PlayerStats.DeathRecord
        {
            Day = 2.5,
            Location = new Engine.Vector3(40f, 50f, 60f),
            Cause = "latest",
        });

        var marker = TravelMapComponent.SelectLastDeath(stats);

        Assert.NotNull(marker);
        Assert.Equal(new Vector3(40f, 50f, 60f), marker.Position);
        Assert.Equal(2.5, marker.Day);
        Assert.Equal("latest", marker.Cause);
    }

    [Fact]
    public void Actual_minimap_draw_queues_shadow_then_two_frames_and_dark_outline_then_red_marker()
    {
        using var widget = CreateSurface();
        var queue = new RecordingMapSurfacePrimitiveQueue();
        widget.ShowSurveyCrosshair = false;
        widget.ShowFrameShadow = true;
        widget.DrawPlayerOutline = true;
        widget.PlayerArrowSize = 18f;
        widget.PlayerMarkerColor = TravelMapPalette.MiniMapPlayer;
        widget.FrameThickness = MiniMapVisualStyle.FrameThickness;
        widget.FrameShadowColor = TravelMapPalette.MiniMapFrameShadow;
        widget.FrameColor = TravelMapPalette.MiniMapFrame;

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        Assert.Collection(
            queue.Primitives,
            background => Assert.Equal(MapSurfacePrimitiveKind.Background, background.Kind),
            outline =>
            {
                Assert.Equal(MapSurfacePrimitiveKind.Player, outline.Kind);
                Assert.Equal(TravelMapPalette.MiniMapPlayerOutline, outline.Color);
                Assert.Equal(21f, outline.Size);
                Assert.Equal(96f - (21f * 0.55f), outline.Vertices[0].Y, 4);
            },
            marker =>
            {
                Assert.Equal(MapSurfacePrimitiveKind.Player, marker.Kind);
                Assert.Equal(TravelMapPalette.MiniMapPlayer, marker.Color);
                Assert.Equal(18f, marker.Size);
                Assert.Equal(96f - (18f * 0.55f), marker.Vertices[0].Y, 4);
            },
            shadow => AssertQueuedRectangle(
                shadow,
                MapSurfacePrimitiveKind.FrameShadow,
                0.5f,
                191.5f,
                TravelMapPalette.MiniMapFrameShadow),
            outerFrame => AssertQueuedRectangle(
                outerFrame,
                MapSurfacePrimitiveKind.Frame,
                1.5f,
                190.5f,
                TravelMapPalette.MiniMapFrame),
            innerFrame => AssertQueuedRectangle(
                innerFrame,
                MapSurfacePrimitiveKind.Frame,
                2.5f,
                189.5f,
                TravelMapPalette.MiniMapFrame));

        Assert.NotEqual(queue.Primitives[1].Color, queue.Primitives[2].Color);
        Assert.Equal(
            MiniMapVisualStyle.FrameShadowAlpha,
            queue.Primitives[3].Color.A);
        Assert.NotEqual(queue.Primitives[3].Color, queue.Primitives[4].Color);
    }

    [Fact]
    public void Heading_up_minimap_keeps_an_east_facing_player_arrow_pointing_up()
    {
        using var widget = CreateSurface(
            orientation: MiniMapOrientation.HeadingUp,
            heading: MathF.PI / 2f);
        widget.ApplyConfiguredMiniMapOrientation = true;
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        var player = Assert.Single(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.Player);
        Assert.Equal(96f, player.Vertices[0].X, 3);
        Assert.True(player.Vertices[0].Y < 96f);
        Assert.Equal(-MathF.PI / 2f, widget.Transform.RotationRadians, 3);
    }

    [Fact]
    public void Large_map_ignores_heading_up_setting_and_remains_north_up()
    {
        using var widget = CreateSurface(
            orientation: MiniMapOrientation.HeadingUp,
            heading: MathF.PI / 2f);
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(192f), queue));

        var player = Assert.Single(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.Player);
        Assert.True(player.Vertices[0].X > 96f);
        Assert.Equal(96f, player.Vertices[0].Y, 3);
        Assert.Equal(0f, widget.Transform.RotationRadians);
    }

    [Fact]
    public void Actual_default_large_map_draw_queues_no_shadow()
    {
        using var widget = CreateSurface();
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(640f, 480f), queue));

        Assert.DoesNotContain(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.FrameShadow);
        var frame = Assert.Single(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.Frame);
        AssertQueuedRectangle(
            frame,
            MapSurfacePrimitiveKind.Frame,
            1f,
            new Vector2(639f, 479f),
            TravelMapPalette.Moss);
    }

    [Fact]
    public void Large_map_stays_rectangular_when_the_minimap_shape_is_circle()
    {
        using var widget = CreateSurface(mapShape: MapShape.Circle);
        var queue = new RecordingMapSurfacePrimitiveQueue();

        widget.Draw(new MapSurfaceDrawContext(new Vector2(640f, 480f), queue));

        var frame = Assert.Single(
            queue.Primitives,
            primitive => primitive.Kind == MapSurfacePrimitiveKind.Frame);
        Assert.Equal(new Vector2(1f), frame.Minimum);
        Assert.Equal(new Vector2(639f, 479f), frame.Maximum);
    }

    [Fact]
    public void Shadow_alpha_has_one_production_definition_and_reaches_the_real_draw_queue()
    {
        var production = string.Concat(
            File.ReadAllText(Path.Combine(
                TestPaths.RepositoryRoot,
                "src",
                "SurvivalcraftTravelMap",
                "UI",
                "MiniMapRenderer.cs")),
            File.ReadAllText(Path.Combine(
                TestPaths.RepositoryRoot,
                "src",
                "SurvivalcraftTravelMap",
                "UI",
                "TravelMapRenderModel.cs")));

        Assert.Equal(1, production.Split("0x80", StringSplitOptions.None).Length - 1);
        Assert.Equal(MiniMapVisualStyle.FrameShadowAlpha, TravelMapPalette.MiniMapFrameShadow.A);
    }

    private static MapSurfaceWidget CreateSurface(
        IReadOnlyList<CreatureMapMarker>? creatures = null,
        bool showCreatures = true,
        MiniMapOrientation orientation = MiniMapOrientation.NorthUp,
        float heading = 0f,
        bool showGameTime = false,
        MapShape mapShape = MapShape.Square,
        bool showCoordinates = false,
        DeathMapMarker? lastDeath = null,
        bool showLastDeath = true)
    {
        var font = (BitmapFont)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(BitmapFont));
        return new MapSurfaceWidget(
            new EmptyPixelSource(),
            new TravelMapSettings
            {
                ShowCoordinates = showCoordinates,
                ShowCreatureMarkers = showCreatures,
                ShowLastDeathMarker = showLastDeath,
                MiniMapOrientation = orientation,
                MiniMapShape = mapShape,
                ShowGameTime = showGameTime,
            },
            () => new PlayerMapPose(new System.Numerics.Vector3(0f, 64f, 0f), heading),
            () => [],
            () => creatures ?? [],
            () => lastDeath,
            () => 1f,
            font);
    }

    private static void AssertQueuedRectangle(
        RecordedMapSurfacePrimitive primitive,
        MapSurfacePrimitiveKind kind,
        float minimum,
        float maximum,
        Rgba32 color) => AssertQueuedRectangle(
            primitive,
            kind,
            minimum,
            new Vector2(maximum),
            color);

    private static void AssertQueuedRectangle(
        RecordedMapSurfacePrimitive primitive,
        MapSurfacePrimitiveKind kind,
        float minimum,
        Vector2 maximum,
        Rgba32 color)
    {
        Assert.Equal(kind, primitive.Kind);
        Assert.Equal(new Vector2(minimum), primitive.Minimum);
        Assert.Equal(maximum, primitive.Maximum);
        Assert.Equal(color, primitive.Color);
    }

    private sealed class RecordingMapFontQueue : IMapFontQueue
    {
        public List<float> Scales { get; } = [];

        public List<(MapTextAlignment Alignment, float Scale)> Items { get; } = [];

        public List<string> Texts { get; } = [];

        public List<Vector2> Positions { get; } = [];

        public void QueueText(
            string text,
            Vector2 position,
            Rgba32 color,
            MapTextAlignment alignment,
            float scale)
        {
            Scales.Add(scale);
            Items.Add((alignment, scale));
            Texts.Add(text);
            Positions.Add(position);
        }
    }

    private sealed class EmptyPixelSource : IExploredMapPixelSource
    {
        public IExploredMapReadSession BeginReadSession() => new EmptySession();

        private sealed class EmptySession : IExploredMapReadSession
        {
            public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
            {
                color = default;
                return false;
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingMapSurfacePrimitiveQueue : IMapSurfacePrimitiveQueue
    {
        public List<RecordedMapSurfacePrimitive> Primitives { get; } = [];

        public void QueueQuad(
            MapSurfacePrimitiveKind kind,
            Vector2 minimum,
            Vector2 maximum,
            Rgba32 color) => Primitives.Add(new RecordedMapSurfacePrimitive(
                kind,
                minimum,
                maximum,
                [minimum, maximum],
                0f,
                color));

        public void QueueQuad(
            MapSurfacePrimitiveKind kind,
            Vector2 point1,
            Vector2 point2,
            Vector2 point3,
            Vector2 point4,
            Rgba32 color)
        {
            var vertices = new[] { point1, point2, point3, point4 };
            Primitives.Add(new RecordedMapSurfacePrimitive(
                kind,
                new Vector2(vertices.Min(point => point.X), vertices.Min(point => point.Y)),
                new Vector2(vertices.Max(point => point.X), vertices.Max(point => point.Y)),
                vertices,
                0f,
                color));
        }

        public void QueueLine(
            MapSurfacePrimitiveKind kind,
            Vector2 start,
            Vector2 end,
            Rgba32 color) => Primitives.Add(new RecordedMapSurfacePrimitive(
                kind,
                Vector2.Min(start, end),
                Vector2.Max(start, end),
                [start, end],
                0f,
                color));

        public void QueueTriangle(
            MapSurfacePrimitiveKind kind,
            MapPlayerPrimitive primitive) => Primitives.Add(new RecordedMapSurfacePrimitive(
                kind,
                Vector2.Min(primitive.Tip, Vector2.Min(primitive.Left, primitive.Right)),
                Vector2.Max(primitive.Tip, Vector2.Max(primitive.Left, primitive.Right)),
                [primitive.Tip, primitive.Left, primitive.Right],
                primitive.Size,
                primitive.Color));

        public void QueueTriangle(
            MapSurfacePrimitiveKind kind,
            Vector2 point1,
            Vector2 point2,
            Vector2 point3,
            Rgba32 color)
        {
            var vertices = new[] { point1, point2, point3 };
            Primitives.Add(new RecordedMapSurfacePrimitive(
                kind,
                new Vector2(vertices.Min(point => point.X), vertices.Min(point => point.Y)),
                new Vector2(vertices.Max(point => point.X), vertices.Max(point => point.Y)),
                vertices,
                0f,
                color));
        }

        public void QueueDisc(
            MapSurfacePrimitiveKind kind,
            Vector2 center,
            float radius,
            Rgba32 color,
            int segments) => Primitives.Add(new RecordedMapSurfacePrimitive(
                kind,
                center - new Vector2(radius),
                center + new Vector2(radius),
                [center],
                radius * 2f,
                color));

        public void QueueRectangle(MapFramePrimitive primitive) =>
            Primitives.Add(new RecordedMapSurfacePrimitive(
                primitive.Kind == MapFramePrimitiveKind.Shadow
                    ? MapSurfacePrimitiveKind.FrameShadow
                    : MapSurfacePrimitiveKind.Frame,
                primitive.Minimum,
                primitive.Maximum,
                [primitive.Minimum, primitive.Maximum],
                0f,
                primitive.Color));
    }

    private sealed record RecordedMapSurfacePrimitive(
        MapSurfacePrimitiveKind Kind,
        Vector2 Minimum,
        Vector2 Maximum,
        IReadOnlyList<Vector2> Vertices,
        float Size,
        Rgba32 Color);
}
