using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TouchGhostFilterTests
{
    private static readonly Vector2 Ghost = new(1500f, 20f);
    private static readonly MapTransform Transform = new(Vector2.Zero, 2f, new Vector2(800f, 600f));

    private static RawMapTouch Touch(int id, float x, float y, MiniMapTouchPhase phase) =>
        new(id, new Vector2(x, y), phase);

    private static TouchMapGestureUpdate Drive(
        TouchMapGestureState gesture,
        IReadOnlyList<RawMapTouch> touches,
        ref MapTransform transform)
    {
        var points = touches
            .Where(touch => touch.Phase != MiniMapTouchPhase.Released)
            .Select(touch => new TouchMapPoint(touch.Id, touch.Position))
            .ToArray();
        var update = gesture.Update(points, transform, 0.25f, 32f);
        if (update.Command.Transform is { } next)
        {
            transform = next;
        }

        return update;
    }

    [Fact]
    public void Unfiltered_ghost_touch_leaves_the_gesture_stuck_forever()
    {
        // The behaviour players hit: a touch whose release the engine dropped (keyboard up) stays in
        // the touch list. One real drag + lift later the map waits for "all fingers up" for good.
        var gesture = new TouchMapGestureState();
        var transform = Transform;
        var ghost = Touch(7, Ghost.X, Ghost.Y, MiniMapTouchPhase.Moved);

        Drive(gesture, [ghost, Touch(1, 400, 300, MiniMapTouchPhase.Pressed)], ref transform);
        Drive(gesture, [ghost, Touch(1, 420, 300, MiniMapTouchPhase.Moved)], ref transform);
        Drive(gesture, [ghost, Touch(1, 420, 300, MiniMapTouchPhase.Released)], ref transform);
        for (var frame = 0; frame < 100; frame++)
        {
            Drive(gesture, [ghost], ref transform);
        }

        Assert.Equal(TouchMapGestureMode.AwaitingRelease, gesture.Mode);
    }

    [Fact]
    public void Ghost_that_was_never_pressed_on_the_map_is_ignored()
    {
        var filter = new TouchGhostFilter();
        var gesture = new TouchMapGestureState();
        var transform = Transform;
        var live = new List<RawMapTouch>();
        var ghost = Touch(7, Ghost.X, Ghost.Y, MiniMapTouchPhase.Moved);
        var time = 0.0;

        void Frame(params RawMapTouch[] raw)
        {
            time += 1.0 / 60;
            filter.Filter(raw, time, windowActive: true, live);
            Drive(gesture, live, ref transform);
        }

        Frame(ghost);
        Assert.Empty(live);

        Frame(ghost, Touch(1, 400, 300, MiniMapTouchPhase.Pressed));
        Assert.Single(live);
        Frame(ghost, Touch(1, 420, 300, MiniMapTouchPhase.Moved));
        Assert.Equal(TouchMapGestureMode.Dragging, gesture.Mode);
        Frame(ghost, Touch(1, 420, 300, MiniMapTouchPhase.Released));
        Frame(ghost);
        Assert.Equal(TouchMapGestureMode.Idle, gesture.Mode);

        // A real pinch afterwards zooms.
        Frame(ghost, Touch(2, 300, 300, MiniMapTouchPhase.Pressed), Touch(3, 500, 300, MiniMapTouchPhase.Pressed));
        Frame(ghost, Touch(2, 200, 300, MiniMapTouchPhase.Moved), Touch(3, 600, 300, MiniMapTouchPhase.Moved));
        Assert.True(transform.BlocksPerPixel < Transform.BlocksPerPixel);
    }

    [Fact]
    public void Single_real_finger_beside_a_ghost_counts_as_one_touch_for_long_press()
    {
        var filter = new TouchGhostFilter();
        var live = new List<RawMapTouch>();
        var ghost = Touch(7, Ghost.X, Ghost.Y, MiniMapTouchPhase.Moved);

        filter.Filter([ghost, Touch(1, 400, 300, MiniMapTouchPhase.Pressed)], 0.0, true, live);
        filter.Filter([ghost, Touch(1, 400, 300, MiniMapTouchPhase.Moved)], 0.3, true, live);

        Assert.Single(live);
        Assert.Equal(1, live[0].Id);
    }

    [Fact]
    public void Release_lost_while_the_map_was_open_is_dropped_when_a_new_finger_goes_down()
    {
        var filter = new TouchGhostFilter();
        var live = new List<RawMapTouch>();

        filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Pressed)], 0.0, true, live);
        // Release never arrives; the touch sits still in the list.
        for (var time = 0.1; time < TouchGhostFilter.StaleSeconds + 1; time += 0.1)
        {
            filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Moved)], time, true, live);
        }

        filter.Filter(
            [Touch(1, 400, 300, MiniMapTouchPhase.Moved), Touch(2, 100, 100, MiniMapTouchPhase.Pressed)],
            TouchGhostFilter.StaleSeconds + 1.1,
            true,
            live);

        Assert.Single(live);
        Assert.Equal(2, live[0].Id);
    }

    [Fact]
    public void Still_finger_held_briefly_still_pinches_with_a_second_finger()
    {
        var filter = new TouchGhostFilter();
        var live = new List<RawMapTouch>();

        filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Pressed)], 0.0, true, live);
        for (var time = 0.1; time < 3.0; time += 0.1)
        {
            filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Moved)], time, true, live);
        }

        filter.Filter(
            [Touch(1, 400, 300, MiniMapTouchPhase.Moved), Touch(2, 500, 300, MiniMapTouchPhase.Pressed)],
            3.05,
            true,
            live);

        Assert.Equal(2, live.Count);
    }

    [Fact]
    public void Suspend_gap_or_inactive_window_forgets_tracked_fingers()
    {
        var filter = new TouchGhostFilter();
        var live = new List<RawMapTouch>();

        filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Pressed)], 0.0, true, live);
        filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Moved)], 5.0, true, live);
        Assert.Empty(live);

        filter.Filter([Touch(2, 400, 300, MiniMapTouchPhase.Pressed)], 5.1, true, live);
        filter.Filter([Touch(2, 400, 300, MiniMapTouchPhase.Moved)], 5.2, false, live);
        Assert.Empty(live);
        Assert.Equal(0, filter.TrackedCount);
    }

    [Fact]
    public void Released_touch_is_reported_once_then_forgotten()
    {
        var filter = new TouchGhostFilter();
        var live = new List<RawMapTouch>();

        filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Pressed)], 0.0, true, live);
        filter.Filter([Touch(1, 400, 300, MiniMapTouchPhase.Released)], 0.1, true, live);
        Assert.Single(live);
        Assert.Equal(MiniMapTouchPhase.Released, live[0].Phase);
        Assert.Equal(0, filter.TrackedCount);
    }
}
