using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal readonly record struct RawMapTouch(int Id, Vector2 Position, MiniMapTouchPhase Phase);

/// <summary>
/// Keeps only the fingers the large map actually saw go down, so a touch the engine never
/// released cannot take over the map's gestures.
/// <para>
/// The engine drops a touch release whenever the window is inactive or the on-screen keyboard is
/// up (<c>Engine.Input.Touch.ProcessTouchReleased</c> returns early), which leaves that touch in
/// <c>TouchLocations</c> for good. Naming a waypoint opens the keyboard from a button press, so
/// the finger that pressed it becomes such a ghost. The gesture code then saw two fingers whenever
/// the player used one: long-press teleport never fired (it needs exactly one), a drag turned into
/// a pinch against the ghost, and once the real finger lifted the pinch waited forever for "all
/// fingers up" — zoom and pan stayed dead until the touch list was cleared by leaving the world.
/// </para>
/// </summary>
internal sealed class TouchGhostFilter
{
    /// <summary>
    /// A frame gap this long means the app was suspended or stalled, and a release may have been
    /// lost in between, so everything tracked is dropped and fingers are re-acquired on their next
    /// press.
    /// </summary>
    public const double SuspendGapSeconds = 1.0;

    /// <summary>
    /// A tracked finger that has not moved for this long when another finger goes down is taken to
    /// be a ghost whose release was lost while the map was open. A real finger reports movement
    /// almost continuously; resting perfectly still this long and then adding a second finger is
    /// rare, and costs only a restarted gesture.
    /// </summary>
    public const double StaleSeconds = 8.0;

    private readonly Dictionary<int, TrackedTouch> _tracked = [];
    private readonly List<int> _scratch = [];
    private double? _lastUpdateTime;

    public int TrackedCount => _tracked.Count;

    public void Reset()
    {
        _tracked.Clear();
        _lastUpdateTime = null;
    }

    /// <summary>
    /// Writes into <paramref name="result"/> the touches that belong to fingers seen going down
    /// while the map was being updated, in input order. Released touches are included on the frame
    /// they are released (consumers need the release edge) and forgotten afterwards.
    /// </summary>
    public void Filter(
        IReadOnlyList<RawMapTouch> raw,
        double time,
        bool windowActive,
        List<RawMapTouch> result)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(result);
        result.Clear();

        if (!windowActive
            || (_lastUpdateTime is { } last && time - last >= SuspendGapSeconds))
        {
            _tracked.Clear();
        }

        _lastUpdateTime = time;

        // Forget fingers the engine no longer reports.
        _scratch.Clear();
        foreach (var id in _tracked.Keys)
        {
            if (!Contains(raw, id))
            {
                _scratch.Add(id);
            }
        }

        foreach (var id in _scratch)
        {
            _tracked.Remove(id);
        }

        if (!windowActive)
        {
            return;
        }

        if (HasNewPress(raw))
        {
            _scratch.Clear();
            foreach (var (id, tracked) in _tracked)
            {
                if (time - tracked.LastMovedTime >= StaleSeconds)
                {
                    _scratch.Add(id);
                }
            }

            foreach (var id in _scratch)
            {
                _tracked.Remove(id);
            }
        }

        for (var index = 0; index < raw.Count; index++)
        {
            var touch = raw[index];
            if (_tracked.TryGetValue(touch.Id, out var tracked))
            {
                if (touch.Phase == MiniMapTouchPhase.Released)
                {
                    _tracked.Remove(touch.Id);
                }
                else if (touch.Position != tracked.Position)
                {
                    _tracked[touch.Id] = new TrackedTouch(touch.Position, time);
                }

                result.Add(touch);
                continue;
            }

            if (touch.Phase == MiniMapTouchPhase.Pressed)
            {
                _tracked[touch.Id] = new TrackedTouch(touch.Position, time);
                result.Add(touch);
            }

            // Anything else is a finger that went down before the map was looking (the tap that
            // opened it, a ghost from an earlier screen) — never adopt it mid-stream.
        }
    }

    private bool HasNewPress(IReadOnlyList<RawMapTouch> raw)
    {
        for (var index = 0; index < raw.Count; index++)
        {
            if (raw[index].Phase == MiniMapTouchPhase.Pressed && !_tracked.ContainsKey(raw[index].Id))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(IReadOnlyList<RawMapTouch> raw, int id)
    {
        for (var index = 0; index < raw.Count; index++)
        {
            if (raw[index].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct TrackedTouch(Vector2 Position, double LastMovedTime);
}
