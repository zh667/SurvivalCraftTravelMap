namespace SurvivalcraftTravelMap.Map;

public sealed class TerrainChunkExplorationScheduler
{
    private readonly LinkedList<TerrainChunkCoordinate> _pending = new();
    private readonly Dictionary<TerrainChunkCoordinate, LinkedListNode<TerrainChunkCoordinate>> _nodes = new();
    private readonly HashSet<TerrainChunkCoordinate> _visible = [];
    private IReadOnlyList<TerrainChunkCoordinate> _visibleNearestFirst = Array.Empty<TerrainChunkCoordinate>();
    private int _coverageCursor;
    private TerrainChunkCoordinate? _center;

    public int PendingCount => _pending.Count;

    public bool ObservePlayerPosition(int worldX, int worldZ)
    {
        var chunk = TerrainChunkCoordinate.FromWorld(worldX, worldZ);
        var changed = !_visible.SetEquals([chunk]);

        foreach (var leaving in _visible.Where(visibleChunk => visibleChunk != chunk).ToArray())
            RemovePending(leaving);

        if (!_visible.Contains(chunk))
            EnqueueLast(chunk);

        _visible.Clear();
        _visible.Add(chunk);
        if (changed)
        {
            _visibleNearestFirst = [chunk];
            _coverageCursor = 0;
        }

        _center = chunk;
        MovePendingToFront(chunk);
        return changed;
    }

    public bool ObserveFootprint(MinimapExplorationFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        var nextVisible = footprint.ChunksNearestFirst.ToHashSet();
        var changed = !_visible.SetEquals(nextVisible);

        foreach (var leaving in _visible.Where(chunk => !nextVisible.Contains(chunk)).ToArray())
            RemovePending(leaving);

        foreach (var entering in footprint.ChunksNearestFirst.Where(chunk => !_visible.Contains(chunk)))
            EnqueueLast(entering);

        _visible.Clear();
        _visible.UnionWith(nextVisible);
        if (changed)
        {
            _visibleNearestFirst = footprint.ChunksNearestFirst.ToArray();
            _coverageCursor = 0;
        }

        _center = footprint.CenterChunk;
        MovePendingToFront(footprint.CenterChunk);
        return changed;
    }

    public int ReconcileCoverage(
        Func<TerrainChunkCoordinate, bool> isFullyExplored,
        int maximumChecks)
    {
        ArgumentNullException.ThrowIfNull(isFullyExplored);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChecks);
        if (_visibleNearestFirst.Count == 0)
            return 0;

        var candidates = new List<TerrainChunkCoordinate>(
            Math.Min(maximumChecks, _visibleNearestFirst.Count));
        if (_center is TerrainChunkCoordinate center)
            candidates.Add(center);

        var examined = 0;
        while (candidates.Count < maximumChecks && examined < _visibleNearestFirst.Count)
        {
            if (_coverageCursor >= _visibleNearestFirst.Count)
                _coverageCursor = 0;

            var candidate = _visibleNearestFirst[_coverageCursor++];
            examined++;
            if (!candidates.Contains(candidate))
                candidates.Add(candidate);
        }

        foreach (var candidate in candidates)
        {
            if (isFullyExplored(candidate))
            {
                MarkCompleted(candidate);
            }
            else if (!_nodes.ContainsKey(candidate))
            {
                EnqueueLast(candidate);
            }
        }

        if (_center is TerrainChunkCoordinate current)
            MovePendingToFront(current);
        return candidates.Count;
    }

    public IReadOnlyList<TerrainChunkCoordinate> GetPendingAttempts(int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        if (_pending.Count == 0)
        {
            return Array.Empty<TerrainChunkCoordinate>();
        }

        var attempts = new List<TerrainChunkCoordinate>(Math.Min(maximumCount, _pending.Count));
        LinkedListNode<TerrainChunkCoordinate>? currentNode = null;
        if (_center is TerrainChunkCoordinate center)
        {
            _nodes.TryGetValue(center, out currentNode);
        }

        if (currentNode is not null)
        {
            attempts.Add(currentNode.Value);
        }

        var olderAttemptCount = Math.Min(
            maximumCount - attempts.Count,
            _pending.Count - attempts.Count);
        for (var index = 0; index < olderAttemptCount; index++)
        {
            var node = FindFirstNonCurrentNode(currentNode);
            attempts.Add(node.Value);
            _pending.Remove(node);
            _pending.AddLast(node);
        }

        return attempts;
    }

    public void MarkCompleted(TerrainChunkCoordinate chunk)
    {
        if (_nodes.Remove(chunk, out var node))
        {
            _pending.Remove(node);
        }
    }

    public void Clear()
    {
        _pending.Clear();
        _nodes.Clear();
        _visible.Clear();
        _visibleNearestFirst = Array.Empty<TerrainChunkCoordinate>();
        _coverageCursor = 0;
        _center = null;
    }

    private void EnqueueLast(TerrainChunkCoordinate chunk)
    {
        if (_nodes.ContainsKey(chunk))
        {
            return;
        }

        var node = _pending.AddLast(chunk);
        _nodes.Add(chunk, node);
    }

    private void RemovePending(TerrainChunkCoordinate chunk)
    {
        if (_nodes.Remove(chunk, out var node))
        {
            _pending.Remove(node);
        }
    }

    private void MovePendingToFront(TerrainChunkCoordinate chunk)
    {
        if (_nodes.TryGetValue(chunk, out var node))
        {
            _pending.Remove(node);
            _pending.AddFirst(node);
        }
    }

    private LinkedListNode<TerrainChunkCoordinate> FindFirstNonCurrentNode(
        LinkedListNode<TerrainChunkCoordinate>? currentNode)
    {
        var node = _pending.First!;
        while (ReferenceEquals(node, currentNode))
        {
            node = node.Next!;
        }

        return node;
    }
}
