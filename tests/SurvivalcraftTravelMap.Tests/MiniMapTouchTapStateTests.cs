using System.Numerics;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MiniMapTouchTapStateTests
{
    [Fact]
    public void ReleaseInsideAfterShortTapActivatesMiniMap()
    {
        var state = new MiniMapTouchTapState();

        var pressed = state.Update(4, new Vector2(20f, 20f), MiniMapTouchPhase.Pressed, true, 12f);
        var released = state.Update(4, new Vector2(24f, 22f), MiniMapTouchPhase.Released, true, 12f);

        Assert.True(pressed.Consumed);
        Assert.True(released.Consumed);
        Assert.True(released.Activate);
        Assert.False(state.IsTracking);
    }

    [Fact]
    public void DragDoesNotActivateMiniMap()
    {
        var state = new MiniMapTouchTapState();
        state.Update(4, new Vector2(20f, 20f), MiniMapTouchPhase.Pressed, true, 12f);
        state.Update(4, new Vector2(50f, 20f), MiniMapTouchPhase.Moved, true, 12f);

        var released = state.Update(4, new Vector2(50f, 20f), MiniMapTouchPhase.Released, true, 12f);

        Assert.True(released.Consumed);
        Assert.False(released.Activate);
    }

    [Fact]
    public void TouchStartingOutsideIsIgnored()
    {
        var state = new MiniMapTouchTapState();

        var pressed = state.Update(4, new Vector2(-5f, 20f), MiniMapTouchPhase.Pressed, false, 12f);
        var released = state.Update(4, new Vector2(20f, 20f), MiniMapTouchPhase.Released, true, 12f);

        Assert.False(pressed.Consumed);
        Assert.False(released.Consumed);
        Assert.False(released.Activate);
    }
}
