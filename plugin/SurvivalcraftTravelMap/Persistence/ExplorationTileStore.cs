using SurvivalcraftTravelMap.Map;

namespace SurvivalcraftTravelMap.Persistence;

public sealed class ExplorationTileStore
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly string _directory;
    private readonly Dictionary<TileKey, CacheEntry> _cache = [];
    private readonly LinkedList<TileKey> _lru = [];
    private readonly HashSet<TileKey> _knownTiles = [];

    // Known tiles are also bucketed into coarse super-cells (SuperCellSize x SuperCellSize tiles)
    // so a viewport query only visits tiles near the view. The always-on minimap draws every frame
    // and used to scan the entire _knownTiles set each time; that set only grows (one entry per
    // 64x64 explored area, reloaded on open), so the scan degraded over long/cave sessions.
    private const int KnownTileSuperCellSize = 16;
    private readonly Dictionary<SuperCellKey, HashSet<TileKey>> _knownTileGrid = [];
    private readonly Dictionary<TileKey, long> _tileMutationVersions = [];
    private readonly Func<string, MapTile, CancellationToken, Task> _writeTile;
    private long _tileMaterializations;
    private long _fileProbeCount;
    private long _diskReadAttempts;
    private long _mutationVersion;
    private bool _isUnderPressure;

    public ExplorationTileStore(
        string directory,
        int capacity = 128,
        TimeSpan? flushInterval = null)
        : this(directory, capacity, flushInterval, WriteTileAsync)
    {
    }

    internal ExplorationTileStore(
        string directory,
        int capacity,
        TimeSpan? flushInterval,
        Func<string, MapTile, CancellationToken, Task> writeTile)
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
        _writeTile = writeTile ?? throw new ArgumentNullException(nameof(writeTile));
        Directory.CreateDirectory(_directory);
        LoadKnownTileCatalog();
        Capacity = capacity;
        FlushInterval = actualFlushInterval;
    }

    public int Capacity { get; }

    public TimeSpan FlushInterval { get; }

    public bool IsUnderPressure
    {
        get
        {
            lock (_sync)
            {
                return _isUnderPressure;
            }
        }
    }

    public ExplorationTileStoreDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new ExplorationTileStoreDiagnostics(
                    _knownTiles.Count,
                    _cache.Count,
                    _tileMaterializations,
                    _fileProbeCount,
                    _diskReadAttempts);
            }
        }
    }

    internal long MutationVersion
    {
        get
        {
            lock (_sync)
            {
                return _mutationVersion;
            }
        }
    }

    internal long GetTileMutationVersion(int tileX, int tileZ)
    {
        lock (_sync)
        {
            return _tileMutationVersions.GetValueOrDefault(new TileKey(tileX, tileZ));
        }
    }

    public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region)
    {
        lock (_sync)
        {
            return EnumerateKnownTilesInRegion(region)
                .OrderBy(key => key.Z)
                .ThenBy(key => key.X)
                .Select(key => new MapTileCoordinate(key.X, key.Z))
                .ToArray();
        }
    }

    public MapTileCatalog GetKnownTileCatalog(MapTileRegion region, int maximumCount)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            var candidates = new List<TileKey>();
            foreach (var key in EnumerateKnownTilesInRegion(region))
            {
                if (candidates.Count == maximumCount)
                {
                    return new MapTileCatalog(SortRegionTiles(candidates), IsTruncated: true);
                }

                candidates.Add(key);
            }

            return new MapTileCatalog(SortRegionTiles(candidates), IsTruncated: false);
        }
    }

    private static MapTileCoordinate[] SortRegionTiles(List<TileKey> tiles) =>
        tiles
            .OrderBy(candidate => candidate.Z)
            .ThenBy(candidate => candidate.X)
            .Select(candidate => new MapTileCoordinate(candidate.X, candidate.Z))
            .ToArray();

    // Visits only the tiles in super-cells overlapping the region, so the cost scales with the
    // viewport size rather than with the total number of tiles ever explored. Falls back to
    // walking existing buckets when the region spans more super-cells than exist (e.g. the large
    // map zoomed far out over a sparse world), which is never worse than the old full scan.
    // Callers fully enumerate this within the _sync lock.
    private IEnumerable<TileKey> EnumerateKnownTilesInRegion(MapTileRegion region)
    {
        if (_knownTileGrid.Count == 0)
        {
            yield break;
        }

        var superMinX = FloorDivide(region.MinimumX, KnownTileSuperCellSize);
        var superMaxX = FloorDivide(region.MaximumX, KnownTileSuperCellSize);
        var superMinZ = FloorDivide(region.MinimumZ, KnownTileSuperCellSize);
        var superMaxZ = FloorDivide(region.MaximumZ, KnownTileSuperCellSize);
        var superSpanX = (long)superMaxX - superMinX + 1L;
        var superSpanZ = (long)superMaxZ - superMinZ + 1L;
        if (superSpanX * superSpanZ > _knownTileGrid.Count)
        {
            foreach (var bucket in _knownTileGrid.Values)
            {
                foreach (var key in bucket)
                {
                    if (region.Contains(key.X, key.Z))
                    {
                        yield return key;
                    }
                }
            }

            yield break;
        }

        for (var superZ = superMinZ; superZ <= superMaxZ; superZ++)
        {
            for (var superX = superMinX; superX <= superMaxX; superX++)
            {
                if (!_knownTileGrid.TryGetValue(new SuperCellKey(superX, superZ), out var bucket))
                {
                    continue;
                }

                foreach (var key in bucket)
                {
                    if (region.Contains(key.X, key.Z))
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    private void AddKnownTile(TileKey key)
    {
        if (!_knownTiles.Add(key))
        {
            return;
        }

        var cell = ToSuperCell(key);
        if (!_knownTileGrid.TryGetValue(cell, out var bucket))
        {
            bucket = [];
            _knownTileGrid.Add(cell, bucket);
        }

        bucket.Add(key);
    }

    private void RemoveKnownTile(TileKey key)
    {
        if (!_knownTiles.Remove(key))
        {
            return;
        }

        var cell = ToSuperCell(key);
        if (_knownTileGrid.TryGetValue(cell, out var bucket)
            && bucket.Remove(key)
            && bucket.Count == 0)
        {
            _knownTileGrid.Remove(cell);
        }
    }

    private static SuperCellKey ToSuperCell(TileKey key) => new(
        FloorDivide(key.X, KnownTileSuperCellSize),
        FloorDivide(key.Z, KnownTileSuperCellSize));

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    public bool ContainsKnownTile(int tileX, int tileZ)
    {
        lock (_sync)
        {
            return _knownTiles.Contains(new TileKey(tileX, tileZ));
        }
    }

    public bool IsRegionFullyExplored(
        int tileX,
        int tileZ,
        int x,
        int z,
        int width,
        int height)
    {
        MapTile.ValidateRegion(x, z, width, height);
        var key = new TileKey(tileX, tileZ);
        MapTile tile;
        lock (_sync)
        {
            if (!_knownTiles.Contains(key))
            {
                return false;
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                tile = cached.Tile;
            }
            else
            {
                if (!MakeRoomForNewEntry())
                {
                    return false;
                }

                tile = Load(key);
                var node = _lru.AddFirst(key);
                _cache.Add(key, new CacheEntry(tile, node));
                TrimCleanEntries();
            }
        }

        return tile.IsRegionFullyExplored(x, z, width, height);
    }

    public bool IsRegionFullyHeightShaded(
        int tileX,
        int tileZ,
        int x,
        int z,
        int width,
        int height)
    {
        MapTile.ValidateRegion(x, z, width, height);
        var key = new TileKey(tileX, tileZ);
        MapTile tile;
        lock (_sync)
        {
            if (!_knownTiles.Contains(key))
            {
                return false;
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                tile = cached.Tile;
            }
            else
            {
                if (!MakeRoomForNewEntry())
                {
                    return false;
                }

                tile = Load(key);
                var node = _lru.AddFirst(key);
                _cache.Add(key, new CacheEntry(tile, node));
                TrimCleanEntries();
            }
        }

        return tile.IsRegionFullyHeightShaded(x, z, width, height);
    }

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
            if (!MakeRoomForNewEntry())
            {
                return tile;
            }

            var node = _lru.AddFirst(key);
            _cache.Add(key, new CacheEntry(tile, node));
            TrimCleanEntries();
            return tile;
        }
    }

    public MutationLease AcquireMutation(int tileX, int tileZ)
    {
        var result = TryAcquireMutation(tileX, tileZ, out var lease);
        return result == TileMutationAdmission.Acquired
            ? lease!
            : throw new InvalidOperationException("Tile cache is under write pressure.");
    }

    public TileMutationAdmission TryAcquireMutation(
        int tileX,
        int tileZ,
        out MutationLease? lease)
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
                if (!MakeRoomForNewEntry())
                {
                    _isUnderPressure = true;
                    lease = null;
                    return TileMutationAdmission.Pressure;
                }

                var tile = Load(key);
                var node = _lru.AddFirst(key);
                entry = new CacheEntry(tile, node);
                _cache.Add(key, entry);
            }

            entry.PinCount++;
            SetDirty(entry);
            TrimCleanEntries();
            lease = new MutationLease(entry.Tile, () => CompleteMutation(key, entry));
            return TileMutationAdmission.Acquired;
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
            AddKnownTile(key);
            _mutationVersion++;
            _tileMutationVersions[key] = _mutationVersion;
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
                await _writeTile(path, pending.Snapshot, cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    if (_cache.TryGetValue(pending.Key, out var entry)
                        && ReferenceEquals(entry.Tile, pending.Original)
                        && entry.Generation == pending.Generation)
                    {
                        entry.IsDirty = false;
                    }

                    TrimCleanEntries();
                    RefreshPressureState();
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
        _tileMaterializations++;
        _fileProbeCount++;
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return new MapTile(key.X, key.Z);
        }

        try
        {
            _diskReadAttempts++;
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
            RemoveKnownTile(key);
            return new MapTile(key.X, key.Z);
        }
    }

    private void LoadKnownTileCatalog()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.sctm", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var separator = name.IndexOf('_');
            if (separator <= 0 || separator == name.Length - 1 || name.IndexOf('_', separator + 1) >= 0)
            {
                continue;
            }

            if (int.TryParse(name.AsSpan(0, separator), out var tileX)
                && int.TryParse(name.AsSpan(separator + 1), out var tileZ))
            {
                AddKnownTile(new TileKey(tileX, tileZ));
            }
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

    private void CompleteMutation(TileKey key, CacheEntry entry)
    {
        lock (_sync)
        {
            SetDirty(entry);
            AddKnownTile(key);
            _mutationVersion++;
            _tileMutationVersions[key] = _mutationVersion;
            entry.PinCount--;
            TrimCleanEntries();
        }
    }

    private void TrimCleanEntries()
    {
        while (_cache.Count > Capacity)
        {
            var candidate = _lru.Last;
            while (candidate is not null
                && (_cache[candidate.Value].IsDirty || _cache[candidate.Value].PinCount > 0))
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

    private bool MakeRoomForNewEntry()
    {
        while (_cache.Count >= Capacity)
        {
            var candidate = _lru.Last;
            while (candidate is not null
                && (_cache[candidate.Value].IsDirty || _cache[candidate.Value].PinCount > 0))
            {
                candidate = candidate.Previous;
            }

            if (candidate is null)
            {
                return false;
            }

            _lru.Remove(candidate);
            _cache.Remove(candidate.Value);
        }

        return true;
    }

    private void RefreshPressureState()
    {
        if (_cache.Count < Capacity
            || _cache.Values.Any(entry => !entry.IsDirty && entry.PinCount == 0))
        {
            _isUnderPressure = false;
        }
    }

    private static Task WriteTileAsync(string path, MapTile tile, CancellationToken cancellationToken) =>
        AtomicFile.ReplaceAsync(
            path,
            (stream, _) =>
            {
                TileCodec.Write(stream, tile);
                return Task.CompletedTask;
            },
            cancellationToken);

    private readonly record struct TileKey(int X, int Z);

    private readonly record struct SuperCellKey(int X, int Z);

    public sealed class MutationLease : IDisposable
    {
        private Action? _complete;

        internal MutationLease(MapTile tile, Action complete)
        {
            Tile = tile;
            _complete = complete;
        }

        public MapTile Tile { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _complete, null)?.Invoke();
        }
    }

    private sealed class CacheEntry(MapTile tile, LinkedListNode<TileKey> node)
    {
        public MapTile Tile { get; } = tile;

        public LinkedListNode<TileKey> Node { get; } = node;

        public bool IsDirty { get; set; }

        public long Generation { get; set; }

        public int PinCount { get; set; }
    }

    private sealed record PendingWrite(TileKey Key, MapTile Original, MapTile Snapshot, long Generation);
}

public readonly record struct MapTileCoordinate(int X, int Z);

public readonly record struct MapTileCatalog(
    IReadOnlyList<MapTileCoordinate> Tiles,
    bool IsTruncated);

public readonly record struct MapTileRegion(int MinimumX, int MaximumX, int MinimumZ, int MaximumZ)
{
    public bool Contains(int tileX, int tileZ) =>
        tileX >= MinimumX && tileX <= MaximumX && tileZ >= MinimumZ && tileZ <= MaximumZ;
}

public readonly record struct ExplorationTileStoreDiagnostics(
    int KnownTileCount,
    int CachedTileCount,
    long TileMaterializations,
    long FileProbeCount,
    long DiskReadAttempts);

public enum TileMutationAdmission
{
    Acquired,
    Pressure,
}
