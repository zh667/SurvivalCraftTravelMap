using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal readonly record struct TouchMapLongPressUpdate(bool Activate, Vector2 Position);

/// <summary>
/// Recognises a stationary press-and-hold on the large map as the touch equivalent of a
/// mouse right-click, so touch players can open the teleport context menu. A gesture fires
/// once, after the finger has stayed within <c>dragThreshold</c> of its start for at least
/// <c>holdDuration</c> seconds; any drag beyond the threshold cancels it (that is a pan),
/// and lifting or a second finger resets the tracker.
/// </summary>
internal sealed class TouchMapLongPressState
{
    private int? _touchId;
    private Vector2 _startPosition;
    private double _startTime;
    private bool _moved;
    private bool _fired;

    public bool IsTracking => _touchId.HasValue;

    public TouchMapLongPressUpdate Update(
        int touchId,
        Vector2 position,
        MiniMapTouchPhase phase,
        double currentTime,
        double holdDuration,
        float dragThreshold)
    {
        if (!double.IsFinite(holdDuration) || holdDuration < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(holdDuration));
        }

        if (!float.IsFinite(dragThreshold) || dragThreshold < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(dragThreshold));
        }

        if (phase == MiniMapTouchPhase.Pressed)
        {
            // A fresh finger while already tracking means a second touch (pinch/zoom);
            // that is never a long press, so stop tracking entirely.
            if (_touchId.HasValue)
            {
                Reset();
                return default;
            }

            _touchId = touchId;
            _startPosition = position;
            _startTime = currentTime;
            _moved = false;
            _fired = false;
            return default;
        }

        if (_touchId != touchId)
        {
            return default;
        }

        if (phase == MiniMapTouchPhase.Released)
        {
            Reset();
            return default;
        }

        _moved |= Vector2.DistanceSquared(_startPosition, position)
            > dragThreshold * dragThreshold;
        if (_moved || _fired || currentTime - _startTime < holdDuration)
        {
            return default;
        }

        _fired = true;
        return new TouchMapLongPressUpdate(Activate: true, Position: _startPosition);
    }

    public void Reset()
    {
        _touchId = null;
        _moved = false;
        _fired = false;
    }
}
