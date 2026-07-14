using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MiniMapTextRendererTests
{
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

    private sealed class RecordingMapFontQueue : IMapFontQueue
    {
        public List<float> Scales { get; } = [];

        public List<(MapTextAlignment Alignment, float Scale)> Items { get; } = [];

        public void QueueText(
            string text,
            Vector2 position,
            Rgba32 color,
            MapTextAlignment alignment,
            float scale)
        {
            Scales.Add(scale);
            Items.Add((alignment, scale));
        }
    }
}
