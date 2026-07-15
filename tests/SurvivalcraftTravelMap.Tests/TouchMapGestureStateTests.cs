using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TouchMapGestureStateTests
{
    [Fact]
    public void SingleFingerDragPansWithoutChangingWorldPointUnderFinger()
    {
        var state = new TouchMapGestureState();
        var transform = new MapTransform(new Vector2(100f, 200f), 2f, new Vector2(800f, 600f));
        var previous = new Vector2(300f, 250f);
        var current = new Vector2(330f, 280f);
        var worldAnchor = transform.ScreenToWorld(previous);

        state.Update([new TouchMapPoint(7, previous)], transform, 0.25f, 32f);
        var update = state.Update([new TouchMapPoint(7, current)], transform, 0.25f, 32f);

        Assert.Equal(TravelMapUiCommandKind.Pan, update.Command.Kind);
        Assert.True(update.Consumed);
        var panned = Assert.IsType<MapTransform>(update.Command.Transform);
        AssertVectorNearlyEqual(worldAnchor, panned.ScreenToWorld(current));
    }

    [Fact]
    public void PinchZoomKeepsWorldAnchorUnderMovingMidpoint()
    {
        var state = new TouchMapGestureState();
        var transform = new MapTransform(new Vector2(100f, 200f), 2f, new Vector2(800f, 600f));
        TouchMapPoint[] start =
        [
            new(1, new Vector2(300f, 240f)),
            new(2, new Vector2(500f, 240f)),
        ];
        TouchMapPoint[] moved =
        [
            new(1, new Vector2(250f, 250f)),
            new(2, new Vector2(570f, 250f)),
        ];
        var oldMidpoint = (start[0].Position + start[1].Position) / 2f;
        var newMidpoint = (moved[0].Position + moved[1].Position) / 2f;
        var worldAnchor = transform.ScreenToWorld(oldMidpoint);

        state.Update(start, transform, 0.25f, 32f);
        var update = state.Update(moved, transform, 0.25f, 32f);

        Assert.Equal(TravelMapUiCommandKind.Zoom, update.Command.Kind);
        var zoomed = Assert.IsType<MapTransform>(update.Command.Transform);
        Assert.True(zoomed.BlocksPerPixel < transform.BlocksPerPixel);
        AssertVectorNearlyEqual(worldAnchor, zoomed.ScreenToWorld(newMidpoint));
    }

    [Fact]
    public void ReleasingOneFingerAfterPinchWaitsForAllTouchesBeforeDragging()
    {
        var state = new TouchMapGestureState();
        var transform = new MapTransform(Vector2.Zero, 1f, new Vector2(800f, 600f));
        TouchMapPoint[] pinch =
        [
            new(1, new Vector2(300f, 250f)),
            new(2, new Vector2(500f, 250f)),
        ];
        state.Update(pinch, transform, 0.25f, 32f);

        var firstOneFingerFrame = state.Update(
            [new TouchMapPoint(1, new Vector2(280f, 250f))],
            transform,
            0.25f,
            32f);
        var movedOneFingerFrame = state.Update(
            [new TouchMapPoint(1, new Vector2(200f, 250f))],
            transform,
            0.25f,
            32f);

        Assert.Equal(TouchMapGestureMode.AwaitingRelease, firstOneFingerFrame.Mode);
        Assert.Null(firstOneFingerFrame.Command.Transform);
        Assert.Equal(TouchMapGestureMode.AwaitingRelease, movedOneFingerFrame.Mode);
        Assert.Null(movedOneFingerFrame.Command.Transform);

        var released = state.Update([], transform, 0.25f, 32f);
        Assert.Equal(TouchMapGestureMode.Idle, released.Mode);

        var freshTouch = state.Update(
            [new TouchMapPoint(3, new Vector2(450f, 300f))],
            transform,
            0.25f,
            32f);
        Assert.Equal(TouchMapGestureMode.Dragging, freshTouch.Mode);
        Assert.Null(freshTouch.Command.Transform);
    }

    private static void AssertVectorNearlyEqual(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 0.001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 0.001f);
    }
}
