namespace SurvivalcraftTravelMap.Map;

public sealed class TerrainChunkExplorationScheduler
{
    private readonly LinkedList<TerrainChunkCoordinate> _pending = new();
    private readonly Dictionary<TerrainChunkCoordinate, LinkedListNode<TerrainChunkCoordinate>> _nodes = new();
    private TerrainChunkCoordinate? _current;

    public int PendingCount => _pending.Count;

    public bool ObservePlayerPosition(int worldX, int worldZ)
    {
        var chunk = TerrainChunkCoordinate.FromWorld(worldX, worldZ);
        if (_current is TerrainChunkCoordinate current && current == chunk)
        {
            return false;
        }

        _current = chunk;
        if (_nodes.TryGetValue(chunk, out var existingNode))
        {
            _pending.Remove(existingNode);
            _pending.AddFirst(existingNode);
        }
        else
        {
            var newNode = _pending.AddFirst(chunk);
            _nodes.Add(chunk, newNode);
        }

        return true;
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
        if (_current is TerrainChunkCoordinate current)
        {
            _nodes.TryGetValue(current, out currentNode);
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
        _current = null;
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
