using System.Numerics;
using SurvivalcraftTravelMap.Map;

namespace SurvivalcraftTravelMap.UI;

internal enum TouchMapGestureMode
{
    Idle,
    Dragging,
    Pinching,
    AwaitingRelease,
}

internal readonly record struct TouchMapPoint(int Id, Vector2 Position);

internal readonly record struct TouchMapGestureUpdate(
    TouchMapGestureMode Mode,
    TravelMapUiCommand Command,
    bool Consumed);

internal sealed class TouchMapGestureState
{
    private int _dragId;
    private Vector2 _lastDragPosition;
    private int _firstPinchId;
    private int _secondPinchId;
    private Vector2 _lastPinchMidpoint;
    private float _lastPinchDistance;

    public TouchMapGestureMode Mode { get; private set; }

    public TouchMapGestureUpdate Update(
        IReadOnlyList<TouchMapPoint> touches,
        MapTransform transform,
        float minimumBlocksPerPixel,
        float maximumBlocksPerPixel)
    {
        ArgumentNullException.ThrowIfNull(touches);
        if (!float.IsFinite(minimumBlocksPerPixel)
            || !float.IsFinite(maximumBlocksPerPixel)
            || minimumBlocksPerPixel <= 0f
            || maximumBlocksPerPixel < minimumBlocksPerPixel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumBlocksPerPixel),
                "The zoom range must be finite, positive, and ordered.");
        }

        return Mode switch
        {
            TouchMapGestureMode.Idle => UpdateIdle(touches),
            TouchMapGestureMode.Dragging => UpdateDragging(touches, transform),
            TouchMapGestureMode.Pinching => UpdatePinching(
                touches,
                transform,
                minimumBlocksPerPixel,
                maximumBlocksPerPixel),
            TouchMapGestureMode.AwaitingRelease => UpdateAwaitingRelease(touches),
            _ => throw new InvalidOperationException("Unknown touch-map gesture mode."),
        };
    }

    public void Reset()
    {
        Mode = TouchMapGestureMode.Idle;
        _lastPinchDistance = 0f;
    }

    private TouchMapGestureUpdate UpdateIdle(IReadOnlyList<TouchMapPoint> touches)
    {
        if (touches.Count >= 2)
        {
            BeginPinch(touches);
            return Consumed();
        }

        if (touches.Count == 1)
        {
            _dragId = touches[0].Id;
            _lastDragPosition = touches[0].Position;
            Mode = TouchMapGestureMode.Dragging;
            return Consumed();
        }

        return NotConsumed();
    }

    private TouchMapGestureUpdate UpdateDragging(
        IReadOnlyList<TouchMapPoint> touches,
        MapTransform transform)
    {
        if (touches.Count >= 2)
        {
            BeginPinch(touches);
            return Consumed();
        }

        if (touches.Count == 0)
        {
            Reset();
            return Consumed();
        }

        var touch = touches[0];
        if (touch.Id != _dragId)
        {
            Mode = TouchMapGestureMode.AwaitingRelease;
            return Consumed();
        }

        var worldUnderPreviousPosition = transform.ScreenToWorld(_lastDragPosition);
        var worldUnderCurrentPosition = transform.ScreenToWorld(touch.Position);
        _lastDragPosition = touch.Position;
        var centerDelta = worldUnderPreviousPosition - worldUnderCurrentPosition;
        if (centerDelta == Vector2.Zero)
        {
            return Consumed();
        }

        return new TouchMapGestureUpdate(
            Mode,
            new TravelMapUiCommand(
                TravelMapUiCommandKind.Pan,
                transform with { Center = transform.Center + centerDelta }),
            Consumed: true);
    }

    private TouchMapGestureUpdate UpdatePinching(
        IReadOnlyList<TouchMapPoint> touches,
        MapTransform transform,
        float minimumBlocksPerPixel,
        float maximumBlocksPerPixel)
    {
        if (!TryFindTouch(touches, _firstPinchId, out var first)
            || !TryFindTouch(touches, _secondPinchId, out var second))
        {
            Mode = TouchMapGestureMode.AwaitingRelease;
            return Consumed();
        }

        var midpoint = (first.Position + second.Position) / 2f;
        var distance = Vector2.Distance(first.Position, second.Position);
        if (!float.IsFinite(distance) || distance < 1f || _lastPinchDistance < 1f)
        {
            _lastPinchMidpoint = midpoint;
            _lastPinchDistance = distance;
            return Consumed();
        }

        var requestedBlocksPerPixel = transform.BlocksPerPixel * (_lastPinchDistance / distance);
        var blocksPerPixel = Math.Clamp(
            requestedBlocksPerPixel,
            minimumBlocksPerPixel,
            maximumBlocksPerPixel);
        var worldAnchor = transform.ScreenToWorld(_lastPinchMidpoint);
        var scaled = transform with { BlocksPerPixel = blocksPerPixel };
        var center = scaled.Center + worldAnchor - scaled.ScreenToWorld(midpoint);
        var changed = center != transform.Center || blocksPerPixel != transform.BlocksPerPixel;

        _lastPinchMidpoint = midpoint;
        _lastPinchDistance = distance;
        if (!changed)
        {
            return Consumed();
        }

        return new TouchMapGestureUpdate(
            Mode,
            new TravelMapUiCommand(
                TravelMapUiCommandKind.Zoom,
                scaled with { Center = center }),
            Consumed: true);
    }

    private TouchMapGestureUpdate UpdateAwaitingRelease(IReadOnlyList<TouchMapPoint> touches)
    {
        if (touches.Count == 0)
        {
            Reset();
        }

        return Consumed();
    }

    private void BeginPinch(IReadOnlyList<TouchMapPoint> touches)
    {
        var first = touches[0];
        var second = touches[1];
        if (second.Id < first.Id)
        {
            (first, second) = (second, first);
        }

        _firstPinchId = first.Id;
        _secondPinchId = second.Id;
        _lastPinchMidpoint = (first.Position + second.Position) / 2f;
        _lastPinchDistance = Vector2.Distance(first.Position, second.Position);
        Mode = TouchMapGestureMode.Pinching;
    }

    private static bool TryFindTouch(
        IReadOnlyList<TouchMapPoint> touches,
        int id,
        out TouchMapPoint result)
    {
        for (var index = 0; index < touches.Count; index++)
        {
            if (touches[index].Id == id)
            {
                result = touches[index];
                return true;
            }
        }

        result = default;
        return false;
    }

    private TouchMapGestureUpdate Consumed() => new(
        Mode,
        TravelMapUiCommand.None,
        Consumed: true);

    private TouchMapGestureUpdate NotConsumed() => new(
        Mode,
        TravelMapUiCommand.None,
        Consumed: false);
}
