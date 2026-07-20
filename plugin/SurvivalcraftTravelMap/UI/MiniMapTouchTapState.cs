using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal enum MiniMapTouchPhase
{
    Pressed,
    Moved,
    Released,
}

internal readonly record struct MiniMapTouchTapUpdate(bool Consumed, bool Activate);

internal sealed class MiniMapTouchTapState
{
    private int? _touchId;
    private Vector2 _startPosition;
    private bool _moved;

    public bool IsTracking => _touchId.HasValue;

    public MiniMapTouchTapUpdate Update(
        int touchId,
        Vector2 position,
        MiniMapTouchPhase phase,
        bool isInside,
        float dragThreshold)
    {
        if (!float.IsFinite(dragThreshold) || dragThreshold < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(dragThreshold));
        }

        if (phase == MiniMapTouchPhase.Pressed)
        {
            if (_touchId.HasValue || !isInside)
            {
                return default;
            }

            _touchId = touchId;
            _startPosition = position;
            _moved = false;
            return new MiniMapTouchTapUpdate(Consumed: true, Activate: false);
        }

        if (_touchId != touchId)
        {
            return default;
        }

        if (phase == MiniMapTouchPhase.Moved)
        {
            _moved |= Vector2.DistanceSquared(_startPosition, position)
                > dragThreshold * dragThreshold;
            return new MiniMapTouchTapUpdate(Consumed: true, Activate: false);
        }

        var activate = !_moved && isInside;
        Reset();
        return new MiniMapTouchTapUpdate(Consumed: true, Activate: activate);
    }

    public void Reset()
    {
        _touchId = null;
        _moved = false;
    }
}
