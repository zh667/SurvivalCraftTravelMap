using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class LargeMapFollowStateTests
{
    [Fact]
    public void Locate_recenters_preserves_zoom_and_enables_follow()
    {
        var state = new LargeMapFollowState();
        var transform = new MapTransform(new Vector2(10f), 3.5f, new Vector2(800f, 480f));

        var located = state.Locate(transform, new Vector2(120f, -40f));

        Assert.True(state.IsFollowing);
        Assert.Equal(new Vector2(120f, -40f), located.Center);
        Assert.Equal(3.5f, located.BlocksPerPixel);
    }

    [Fact]
    public void Follow_tracks_player_until_manual_pan_or_zoom()
    {
        var state = new LargeMapFollowState();
        var transform = state.Locate(
            new MapTransform(Vector2.Zero, 2f, new Vector2(800f, 480f)),
            new Vector2(1f, 2f));

        transform = state.Update(transform, new Vector2(8f, 9f));
        Assert.Equal(new Vector2(8f, 9f), transform.Center);

        state.ObserveManualNavigation(TravelMapUiCommandKind.Pan);
        var unchanged = state.Update(transform, new Vector2(20f, 30f));

        Assert.False(state.IsFollowing);
        Assert.Equal(transform, unchanged);
    }

    [Fact]
    public void Non_navigation_commands_do_not_disable_follow()
    {
        var state = new LargeMapFollowState();
        state.Locate(new MapTransform(), Vector2.Zero);

        state.ObserveManualNavigation(TravelMapUiCommandKind.ShowGroundMenu);

        Assert.True(state.IsFollowing);
    }
}
