using System.Numerics;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TouchMapLongPressStateTests
{
    private const double Hold = 0.5d;
    private const float Drag = 12f;

    [Fact]
    public void Stationary_hold_past_the_duration_activates_once_at_the_start_position()
    {
        var state = new TouchMapLongPressState();
        var start = new Vector2(120f, 80f);

        Assert.False(state.Update(7, start, MiniMapTouchPhase.Pressed, 10.0d, Hold, Drag).Activate);
        Assert.False(state.Update(7, start, MiniMapTouchPhase.Moved, 10.3d, Hold, Drag).Activate);

        var fired = state.Update(7, new Vector2(123f, 82f), MiniMapTouchPhase.Moved, 10.5d, Hold, Drag);

        Assert.True(fired.Activate);
        Assert.Equal(start, fired.Position);
    }

    [Fact]
    public void Only_fires_a_single_time_while_the_finger_stays_down()
    {
        var state = new TouchMapLongPressState();
        state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Pressed, 10.0d, Hold, Drag);

        var first = state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Moved, 10.6d, Hold, Drag);
        var second = state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Moved, 11.0d, Hold, Drag);

        Assert.True(first.Activate);
        Assert.False(second.Activate);
    }

    [Fact]
    public void Dragging_beyond_the_threshold_cancels_the_long_press()
    {
        var state = new TouchMapLongPressState();
        state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Pressed, 10.0d, Hold, Drag);
        state.Update(7, new Vector2(160f, 80f), MiniMapTouchPhase.Moved, 10.1d, Hold, Drag);

        var afterHold = state.Update(7, new Vector2(160f, 80f), MiniMapTouchPhase.Moved, 10.9d, Hold, Drag);

        Assert.False(afterHold.Activate);
    }

    [Fact]
    public void Releasing_before_the_duration_does_not_activate()
    {
        var state = new TouchMapLongPressState();
        state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Pressed, 10.0d, Hold, Drag);

        var released = state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Released, 10.2d, Hold, Drag);

        Assert.False(released.Activate);
        Assert.False(state.IsTracking);
    }

    [Fact]
    public void A_second_finger_cancels_tracking_so_a_pinch_never_teleports()
    {
        var state = new TouchMapLongPressState();
        state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Pressed, 10.0d, Hold, Drag);

        var secondFinger = state.Update(8, new Vector2(300f, 200f), MiniMapTouchPhase.Pressed, 10.1d, Hold, Drag);

        Assert.False(secondFinger.Activate);
        Assert.False(state.IsTracking);
    }

    [Fact]
    public void Events_from_another_touch_id_are_ignored()
    {
        var state = new TouchMapLongPressState();
        state.Update(7, new Vector2(120f, 80f), MiniMapTouchPhase.Pressed, 10.0d, Hold, Drag);

        var otherId = state.Update(9, new Vector2(120f, 80f), MiniMapTouchPhase.Moved, 10.9d, Hold, Drag);

        Assert.False(otherId.Activate);
        Assert.True(state.IsTracking);
    }

    [Theory]
    [InlineData(-1d, 12f)]
    [InlineData(double.NaN, 12f)]
    [InlineData(0.5d, -1f)]
    [InlineData(0.5d, float.PositiveInfinity)]
    public void Invalid_thresholds_are_rejected(double holdDuration, float dragThreshold)
    {
        var state = new TouchMapLongPressState();

        Assert.ThrowsAny<ArgumentException>(() => state.Update(
            7,
            Vector2.Zero,
            MiniMapTouchPhase.Pressed,
            0d,
            holdDuration,
            dragThreshold));
    }
}
