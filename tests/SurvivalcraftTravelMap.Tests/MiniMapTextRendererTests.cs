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

    private sealed class RecordingMapFontQueue : IMapFontQueue
    {
        public List<float> Scales { get; } = [];

        public void QueueText(
            string text,
            Vector2 position,
            Rgba32 color,
            MapTextAlignment alignment,
            float scale) => Scales.Add(scale);
    }
}
