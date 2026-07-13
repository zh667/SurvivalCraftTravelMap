using SurvivalcraftTravelMap.Map;

namespace SurvivalcraftTravelMap.Persistence;

public sealed class ExplorationTileStore
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly string _directory;
    private readonly Dictionary<TileKey, CacheEntry> _cache = [];
    private readonly LinkedList<TileKey> _lru = [];

    public ExplorationTileStore(
        string directory,
        int capacity = 128,
        TimeSpan? flushInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var actualFlushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        if (actualFlushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }

        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        Capacity = capacity;
        FlushInterval = actualFlushInterval;
    }

    public int Capacity { get; }

    public TimeSpan FlushInterval { get; }

    public MapTile GetOrLoad(int tileX, int tileZ)
    {
        var key = new TileKey(tileX, tileZ);
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                return cached.Tile;
            }

            var tile = Load(key);
            var node = _lru.AddFirst(key);
            _cache.Add(key, new CacheEntry(tile, node));
            TrimCleanEntries();
            return tile;
        }
    }

    public MapTile GetOrLoadAndMarkDirty(int tileX, int tileZ)
    {
        var key = new TileKey(tileX, tileZ);
        lock (_sync)
        {
            CacheEntry entry;
            if (_cache.TryGetValue(key, out var cached))
            {
                entry = cached;
            }
            else
            {
                var tile = Load(key);
                var node = _lru.AddFirst(key);
                entry = new CacheEntry(tile, node);
                _cache.Add(key, entry);
            }

            SetDirty(entry);
            TrimCleanEntries();
            return entry.Tile;
        }
    }

    public void MarkDirty(MapTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var key = new TileKey(tile.TileX, tile.TileZ);

        lock (_sync)
        {
            if (!_cache.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Tile, tile))
            {
                throw new InvalidOperationException("Only a tile returned by this store can be marked dirty.");
            }

            SetDirty(entry);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PendingWrite> pendingWrites;
            lock (_sync)
            {
                pendingWrites = _cache
                    .Where(pair => pair.Value.IsDirty)
                    .Select(pair => new PendingWrite(
                        pair.Key,
                        pair.Value.Tile,
                        pair.Value.Tile.CreateSnapshot(),
                        pair.Value.Generation))
                    .ToList();
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pending in pendingWrites)
            {
                var path = GetPath(pending.Key);
                await AtomicFile.ReplaceAsync(
                    path,
                    (stream, _) =>
                    {
                        TileCodec.Write(stream, pending.Snapshot);
                        return Task.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    if (_cache.TryGetValue(pending.Key, out var entry)
                        && ReferenceEquals(entry.Tile, pending.Original)
                        && entry.Generation == pending.Generation)
                    {
                        entry.IsDirty = false;
                    }

                    TrimCleanEntries();
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private MapTile Load(TileKey key)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return new MapTile(key.X, key.Z);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var tile = TileCodec.Read(stream);
            if (tile.TileX != key.X || tile.TileZ != key.Z)
            {
                throw new InvalidDataException("Tile coordinates do not match its file name.");
            }

            return tile;
        }
        catch (InvalidDataException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new MapTile(key.X, key.Z);
        }
    }

    private string GetPath(TileKey key) => Path.Combine(_directory, $"{key.X}_{key.Z}.sctm");

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void SetDirty(CacheEntry entry)
    {
        entry.IsDirty = true;
        entry.Generation++;
        Touch(entry);
    }

    private void TrimCleanEntries()
    {
        while (_cache.Count > Capacity)
        {
            var candidate = _lru.Last;
            while (candidate is not null && _cache[candidate.Value].IsDirty)
            {
                candidate = candidate.Previous;
            }

            if (candidate is null)
            {
                return;
            }

            _lru.Remove(candidate);
            _cache.Remove(candidate.Value);
        }
    }

    private readonly record struct TileKey(int X, int Z);

    private sealed class CacheEntry(MapTile tile, LinkedListNode<TileKey> node)
    {
        public MapTile Tile { get; } = tile;

        public LinkedListNode<TileKey> Node { get; } = node;

        public bool IsDirty { get; set; }

        public long Generation { get; set; }
    }

    private sealed record PendingWrite(TileKey Key, MapTile Original, MapTile Snapshot, long Generation);
}
