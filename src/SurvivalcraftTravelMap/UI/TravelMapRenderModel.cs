using System.Globalization;
using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.Waypoints;

namespace SurvivalcraftTravelMap.UI;

public static class TravelMapPalette
{
    public static Rgba32 Basalt { get; } = new(0x1B, 0x26, 0x28, 0xFF);

    public static Rgba32 Moss { get; } = new(0x6F, 0x8A, 0x3B, 0xFF);

    public static Rgba32 SurveyCyan { get; } = new(0x74, 0xC9, 0xC8, 0xFF);

    public static Rgba32 HazardAmber { get; } = new(0xE2, 0xA3, 0x3B, 0xFF);

    public static Rgba32 CompassNorth { get; } = HazardAmber;

    public static Rgba32 SnowText { get; } = new(0xE8, 0xEC, 0xE7, 0xFF);

    public static Rgba32 MiniMapBackground { get; } = new(0x27, 0x29, 0x2A, 0xFF);

    public static Rgba32 MiniMapFrame { get; } = new(0xE3, 0xDE, 0xD3, 0xFF);

    public static Rgba32 MiniMapFrameShadow { get; } = new(
        0x12,
        0x12,
        0x12,
        MiniMapVisualStyle.FrameShadowAlpha);

    public static Rgba32 MiniMapPlayer { get; } = new(0xD9, 0x43, 0x43, 0xFF);

    public static Rgba32 MiniMapPlayerOutline { get; } = new(0x2A, 0x1C, 0x1C, 0xFF);

    public static Rgba32 DeathMarkerBone { get; } = new(0xEE, 0xEA, 0xD8, 0xFF);

    public static Rgba32 DeathMarkerOutline { get; } = new(0x35, 0x20, 0x20, 0xFF);

    public static Rgba32 MiniMapCoordinateBackdrop { get; } = new(0x12, 0x12, 0x12, 0xA0);
}

public interface IExploredMapPixelSource
{
    IExploredMapReadSession BeginReadSession();
}

public interface IExploredMapReadSession : IDisposable
{
    bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color);

    bool TryGetExploredTerrainPixel(int worldX, int worldZ, out MapTerrainPixel pixel)
    {
        var found = TryGetExploredPixel(worldX, worldZ, out var color);
        pixel = found ? new MapTerrainPixel(color, TerrainHeightShading.Unknown) : default;
        return found;
    }
}

internal interface IExploredMapAggregateReadSession
{
    bool TryGetExploredRegion(
        int worldX,
        int worldZ,
        int width,
        int height,
        out Rgba32 color);

    bool TryGetExploredTerrainRegion(
        int worldX,
        int worldZ,
        int width,
        int height,
        out MapTerrainPixel pixel)
    {
        var found = TryGetExploredRegion(worldX, worldZ, width, height, out var color);
        pixel = found ? new MapTerrainPixel(color, TerrainHeightShading.Unknown) : default;
        return found;
    }
}

public interface ITravelMapRenderSink
{
    void TerrainCell(MapTerrainCell cell);

    void ExplorationBoundary(MapBoundaryEdge edge);

    void Player(Vector3 position, float heading, float size, Rgba32 color);

    void Waypoint(Waypoint waypoint, Rgba32 color);

    void LastDeath(DeathMapMarker marker, Rgba32 color)
    {
    }

    void Label(string text, Vector3 worldPosition, Rgba32 color);
}

public readonly record struct MapTerrainCell(
    int WorldX,
    int WorldZ,
    Vector2 ScreenTopLeft,
    Vector2 ScreenTopRight,
    Vector2 ScreenBottomRight,
    Vector2 ScreenBottomLeft,
    Rgba32 Color)
{
    public Vector2 ScreenMinimum => Vector2.Min(
        Vector2.Min(ScreenTopLeft, ScreenTopRight),
        Vector2.Min(ScreenBottomRight, ScreenBottomLeft));

    public Vector2 ScreenMaximum => Vector2.Max(
        Vector2.Max(ScreenTopLeft, ScreenTopRight),
        Vector2.Max(ScreenBottomRight, ScreenBottomLeft));
}

public readonly record struct MapBoundaryEdge(Vector2 Start, Vector2 End, Rgba32 Color);

public readonly record struct MapOverlayState(
    Vector3 PlayerPosition,
    float PlayerHeading,
    float PlayerArrowSize,
    IReadOnlyList<Waypoint> Waypoints,
    bool ShowCoordinates,
    Rgba32? PlayerMarkerColor)
{
    public DeathMapMarker? LastDeath { get; init; }

    public MapOverlayState(
        Vector3 PlayerPosition,
        float PlayerHeading,
        float PlayerArrowSize,
        IReadOnlyList<Waypoint> Waypoints,
        bool ShowCoordinates)
        : this(
            PlayerPosition,
            PlayerHeading,
            PlayerArrowSize,
            Waypoints,
            ShowCoordinates,
            PlayerMarkerColor: null)
    {
    }

    public void Deconstruct(
        out Vector3 PlayerPosition,
        out float PlayerHeading,
        out float PlayerArrowSize,
        out IReadOnlyList<Waypoint> Waypoints,
        out bool ShowCoordinates)
    {
        PlayerPosition = this.PlayerPosition;
        PlayerHeading = this.PlayerHeading;
        PlayerArrowSize = this.PlayerArrowSize;
        Waypoints = this.Waypoints;
        ShowCoordinates = this.ShowCoordinates;
    }
}

internal interface IMapTileProvider
{
    MapTile GetOrLoad(int tileX, int tileZ);

    bool IsKnownTile(int tileX, int tileZ) => true;

    IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region) => [];

    MapTileCatalog GetKnownTileCatalog(MapTileRegion region, int maximumCount)
    {
        var tiles = GetKnownTiles(region);
        return tiles.Count <= maximumCount
            ? new MapTileCatalog(tiles, IsTruncated: false)
            : new MapTileCatalog(tiles.Take(maximumCount).ToArray(), IsTruncated: true);
    }

    long MutationVersion => 0;

    long GetTileMutationVersion(int tileX, int tileZ) => MutationVersion;
}

public interface IExploredMapTileIndexSource : IExploredMapPixelSource
{
    IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region);
}

internal interface IExploredMapLodSource
{
    IExploredMapReadSession BeginLodReadSession(
        IReadOnlyList<MapTileSamplePlan> plans,
        int stride,
        int maximumNewTiles);
}

internal interface IBoundedExploredMapTileIndexSource
{
    MapTileCatalog GetKnownTileCatalog(MapTileRegion region, int maximumCount);
}

internal readonly record struct MapTileSamplePlan(
    MapTileCoordinate Tile,
    int StartX,
    int EndX,
    int StartZ,
    int EndZ,
    long SampleCount);

public sealed class TileStoreMapPixelSource :
    IExploredMapTileIndexSource,
    IExploredMapLodSource,
    IBoundedExploredMapTileIndexSource
{
    public const int MaximumLodTileMaterializationsPerFrame = 512;

    private readonly object _sync = new();
    private readonly IMapTileProvider _provider;
    private readonly int _snapshotCapacity;
    private readonly Dictionary<(int X, int Z), SnapshotCacheEntry> _snapshotCache = [];
    private readonly LinkedList<(int X, int Z)> _snapshotLru = [];
    private readonly Dictionary<LodCacheKey, LodCacheEntry> _lodCache = [];
    private readonly LinkedList<LodCacheKey> _lodLru = [];
    private int _cachedLodSampleCount;

    public TileStoreMapPixelSource(ExplorationTileStore store, int snapshotCapacity = 128)
        : this(new StoreTileProvider(store), snapshotCapacity)
    {
    }

    internal TileStoreMapPixelSource(IMapTileProvider provider, int snapshotCapacity = 128)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (snapshotCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotCapacity));
        }

        _snapshotCapacity = snapshotCapacity;
    }

    internal long SnapshotCloneCount { get; private set; }

    internal int CachedLodSampleCount
    {
        get
        {
            lock (_sync)
            {
                return _cachedLodSampleCount;
            }
        }
    }

    public IExploredMapReadSession BeginReadSession() => new ReadSession(this);

    public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region) =>
        _provider.GetKnownTiles(region);

    MapTileCatalog IBoundedExploredMapTileIndexSource.GetKnownTileCatalog(
        MapTileRegion region,
        int maximumCount) => _provider.GetKnownTileCatalog(region, maximumCount);

    IExploredMapReadSession IExploredMapLodSource.BeginLodReadSession(
        IReadOnlyList<MapTileSamplePlan> plans,
        int stride,
        int maximumNewTiles)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        if (maximumNewTiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNewTiles));
        }

        if (plans.Sum(plan => plan.SampleCount) > TravelMapRenderModel.MaximumTerrainSamplesPerFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(plans));
        }

        return new LodReadSession(this, plans, stride, maximumNewTiles);
    }

    private LodCacheEntry? GetOrBuildLodEntry(
        MapTileSamplePlan plan,
        int stride,
        ref int remainingNewTiles)
    {
        lock (_sync)
        {
            var key = LodCacheKey.From(plan, stride);
            var mutationVersion = _provider.GetTileMutationVersion(plan.Tile.X, plan.Tile.Z);
            if (_lodCache.TryGetValue(key, out var cached))
            {
                if (cached.MutationVersion == mutationVersion)
                {
                    TouchLod(cached);
                    return cached;
                }

                RemoveLod(key, cached);
            }

            if (remainingNewTiles == 0)
            {
                return null;
            }

            remainingNewTiles--;
            var generation = mutationVersion;
            var snapshot = GetSnapshot(plan.Tile.X, plan.Tile.Z);
            var columns = checked(((plan.EndX - plan.StartX) / stride) + 1);
            var rows = checked(((plan.EndZ - plan.StartZ) / stride) + 1);
            var cells = new LodCell[checked(columns * rows)];
            var index = 0;
            for (long worldZ = plan.StartZ; worldZ <= plan.EndZ; worldZ += stride)
            {
                var localZ = TileCoordinate.FromWorld(0, checked((int)worldZ)).LocalZ;
                for (long worldX = plan.StartX; worldX <= plan.EndX; worldX += stride)
                {
                    var localX = TileCoordinate.FromWorld(checked((int)worldX), 0).LocalX;
                    var width = Math.Min(stride, plan.EndX - worldX + 1);
                    var height = Math.Min(stride, plan.EndZ - worldZ + 1);
                    var found = stride == 1
                        ? snapshot.TryGetTerrainPixel(localX, localZ, out var pixel)
                        : snapshot.TryGetExploredTerrainRegion(
                            localX,
                            localZ,
                            checked((int)width),
                            checked((int)height),
                            out pixel);
                    cells[index++] = new LodCell(found, pixel);
                }
            }

            if (_provider.GetTileMutationVersion(plan.Tile.X, plan.Tile.Z) != generation)
            {
                return null;
            }

            while (_lodLru.Last is { } last
                   && _cachedLodSampleCount
                   > TravelMapRenderModel.MaximumTerrainSamplesPerFrame - cells.Length)
            {
                RemoveLod(last.Value, _lodCache[last.Value]);
            }

            var node = _lodLru.AddFirst(key);
            var created = new LodCacheEntry(plan, stride, columns, cells, generation, node);
            _lodCache.Add(key, created);
            _cachedLodSampleCount += created.SampleCount;
            return created;
        }
    }

    private void TouchLod(LodCacheEntry entry)
    {
        _lodLru.Remove(entry.Node);
        _lodLru.AddFirst(entry.Node);
    }

    private void RemoveLod(LodCacheKey key, LodCacheEntry entry)
    {
        _lodCache.Remove(key);
        _lodLru.Remove(entry.Node);
        _cachedLodSampleCount -= entry.SampleCount;
    }

    private MapTileSnapshot GetSnapshot(int tileX, int tileZ)
    {
        var tile = _provider.GetOrLoad(tileX, tileZ);
        var key = (tileX, tileZ);
        lock (_sync)
        {
            if (_snapshotCache.TryGetValue(key, out var cached)
                && ReferenceEquals(cached.Tile, tile)
                && cached.Version == tile.Version)
            {
                Touch(cached);
                return cached.Snapshot;
            }

            var captured = tile.CreateVersionedSnapshot();
            SnapshotCloneCount++;
            if (cached is not null)
            {
                _snapshotLru.Remove(cached.Node);
                _snapshotCache.Remove(key);
            }

            var node = _snapshotLru.AddFirst(key);
            _snapshotCache.Add(key, new SnapshotCacheEntry(tile, captured.Version, captured.Snapshot, node));
            while (_snapshotCache.Count > _snapshotCapacity)
            {
                var last = _snapshotLru.Last!;
                _snapshotCache.Remove(last.Value);
                _snapshotLru.RemoveLast();
            }

            return captured.Snapshot;
        }
    }

    private void Touch(SnapshotCacheEntry entry)
    {
        _snapshotLru.Remove(entry.Node);
        _snapshotLru.AddFirst(entry.Node);
    }

    private sealed class ReadSession(TileStoreMapPixelSource source) :
        IExploredMapReadSession,
        IExploredMapAggregateReadSession
    {
        private readonly Dictionary<(int X, int Z), MapTileSnapshot> _tiles = [];
        private readonly HashSet<(int X, int Z)> _unknownTiles = [];

        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
        {
            var found = TryGetExploredTerrainPixel(worldX, worldZ, out var pixel);
            color = pixel.Color;
            return found;
        }

        public bool TryGetExploredTerrainPixel(
            int worldX,
            int worldZ,
            out MapTerrainPixel pixel)
        {
            var coordinate = TileCoordinate.FromWorld(worldX, worldZ);
            var key = (coordinate.TileX, coordinate.TileZ);
            if (_unknownTiles.Contains(key))
            {
                pixel = default;
                return false;
            }

            if (!_tiles.TryGetValue(key, out var snapshot))
            {
                if (!source._provider.IsKnownTile(key.TileX, key.TileZ))
                {
                    _unknownTiles.Add(key);
                    pixel = default;
                    return false;
                }

                snapshot = source.GetSnapshot(key.TileX, key.TileZ);
                _tiles.Add(key, snapshot);
            }

            return snapshot.TryGetTerrainPixel(coordinate.LocalX, coordinate.LocalZ, out pixel);
        }

        public bool TryGetExploredRegion(
            int worldX,
            int worldZ,
            int width,
            int height,
            out Rgba32 color)
        {
            var found = TryGetExploredTerrainRegion(worldX, worldZ, width, height, out var pixel);
            color = pixel.Color;
            return found;
        }

        public bool TryGetExploredTerrainRegion(
            int worldX,
            int worldZ,
            int width,
            int height,
            out MapTerrainPixel pixel)
        {
            var coordinate = TileCoordinate.FromWorld(worldX, worldZ);
            if (width <= 0
                || height <= 0
                || width > MapTile.Size - coordinate.LocalX
                || height > MapTile.Size - coordinate.LocalZ)
            {
                pixel = default;
                return false;
            }

            var key = (coordinate.TileX, coordinate.TileZ);
            if (_unknownTiles.Contains(key))
            {
                pixel = default;
                return false;
            }

            if (!_tiles.TryGetValue(key, out var snapshot))
            {
                if (!source._provider.IsKnownTile(key.TileX, key.TileZ))
                {
                    _unknownTiles.Add(key);
                    pixel = default;
                    return false;
                }

                snapshot = source.GetSnapshot(key.TileX, key.TileZ);
                _tiles.Add(key, snapshot);
            }

            return snapshot.TryGetExploredTerrainRegion(
                coordinate.LocalX,
                coordinate.LocalZ,
                width,
                height,
                out pixel);
        }

        public void Dispose()
        {
            _tiles.Clear();
            _unknownTiles.Clear();
        }
    }

    private sealed class LodReadSession(
        TileStoreMapPixelSource source,
        IReadOnlyList<MapTileSamplePlan> plans,
        int stride,
        int maximumNewTiles) :
        IExploredMapReadSession,
        IExploredMapAggregateReadSession
    {
        private readonly Dictionary<(int X, int Z), MapTileSamplePlan> _plans = plans.ToDictionary(
            plan => (plan.Tile.X, plan.Tile.Z));
        private int _remainingNewTiles = maximumNewTiles;

        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color) =>
            TryGetColor(worldX, worldZ, 1, 1, out color);

        public bool TryGetExploredTerrainPixel(
            int worldX,
            int worldZ,
            out MapTerrainPixel pixel) => TryGet(worldX, worldZ, 1, 1, out pixel);

        public bool TryGetExploredRegion(
            int worldX,
            int worldZ,
            int width,
            int height,
            out Rgba32 color) => TryGetColor(worldX, worldZ, width, height, out color);

        public bool TryGetExploredTerrainRegion(
            int worldX,
            int worldZ,
            int width,
            int height,
            out MapTerrainPixel pixel) => TryGet(worldX, worldZ, width, height, out pixel);

        public void Dispose()
        {
            _plans.Clear();
        }

        private bool TryGetColor(
            int worldX,
            int worldZ,
            int width,
            int height,
            out Rgba32 color)
        {
            var found = TryGet(worldX, worldZ, width, height, out var pixel);
            color = pixel.Color;
            return found;
        }

        private bool TryGet(
            int worldX,
            int worldZ,
            int width,
            int height,
            out MapTerrainPixel pixel)
        {
            var coordinate = TileCoordinate.FromWorld(worldX, worldZ);
            if (!_plans.TryGetValue((coordinate.TileX, coordinate.TileZ), out var plan))
            {
                pixel = default;
                return false;
            }

            var entry = source.GetOrBuildLodEntry(plan, stride, ref _remainingNewTiles);
            if (entry is null)
            {
                pixel = default;
                return false;
            }

            return entry.TryGet(worldX, worldZ, width, height, out pixel);
        }
    }

    private sealed class StoreTileProvider(ExplorationTileStore store) : IMapTileProvider
    {
        public ExplorationTileStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

        public MapTile GetOrLoad(int tileX, int tileZ) => Store.GetOrLoad(tileX, tileZ);

        public bool IsKnownTile(int tileX, int tileZ) => Store.ContainsKnownTile(tileX, tileZ);

        public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region) =>
            Store.GetKnownTiles(region);

        public MapTileCatalog GetKnownTileCatalog(MapTileRegion region, int maximumCount) =>
            Store.GetKnownTileCatalog(region, maximumCount);

        public long MutationVersion => Store.MutationVersion;

        public long GetTileMutationVersion(int tileX, int tileZ) =>
            Store.GetTileMutationVersion(tileX, tileZ);
    }

    private readonly record struct LodCacheKey(
        int TileX,
        int TileZ,
        int StartX,
        int EndX,
        int StartZ,
        int EndZ,
        int Stride)
    {
        public static LodCacheKey From(MapTileSamplePlan plan, int stride) => new(
            plan.Tile.X,
            plan.Tile.Z,
            plan.StartX,
            plan.EndX,
            plan.StartZ,
            plan.EndZ,
            stride);
    }

    private readonly record struct LodCell(bool IsExplored, MapTerrainPixel Pixel);

    private sealed class LodCacheEntry(
        MapTileSamplePlan plan,
        int stride,
        int columns,
        LodCell[] cells,
        long mutationVersion,
        LinkedListNode<LodCacheKey> node)
    {
        public int SampleCount => cells.Length;

        public long MutationVersion { get; } = mutationVersion;

        public LinkedListNode<LodCacheKey> Node { get; } = node;

        public bool TryGet(
            int worldX,
            int worldZ,
            int width,
            int height,
            out MapTerrainPixel pixel)
        {
            var offsetX = worldX - plan.StartX;
            var offsetZ = worldZ - plan.StartZ;
            if (offsetX < 0
                || offsetZ < 0
                || worldX > plan.EndX
                || worldZ > plan.EndZ
                || offsetX % stride != 0
                || offsetZ % stride != 0
                || width != Math.Min(stride, plan.EndX - worldX + 1)
                || height != Math.Min(stride, plan.EndZ - worldZ + 1))
            {
                pixel = default;
                return false;
            }

            var index = checked(((offsetZ / stride) * columns) + (offsetX / stride));
            var cell = cells[index];
            pixel = cell.Pixel;
            return cell.IsExplored;
        }
    }

    private sealed class SnapshotCacheEntry(
        MapTile tile,
        long version,
        MapTileSnapshot snapshot,
        LinkedListNode<(int X, int Z)> node)
    {
        public MapTile Tile { get; } = tile;
        public long Version { get; } = version;
        public MapTileSnapshot Snapshot { get; } = snapshot;
        public LinkedListNode<(int X, int Z)> Node { get; } = node;
    }
}

public static class TravelMapRenderModel
{
    public const int MaximumTerrainSamplesPerFrame = 262_144;
    public const int MaximumIndexedTileDescriptorsPerFrame = MaximumTerrainSamplesPerFrame;

    public static MapRenderStatistics RenderTerrain(
        IExploredMapPixelSource source,
        MapTransform transform,
        float brightness,
        ITravelMapRenderSink sink,
        bool useHeightShading = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        if (!float.IsFinite(transform.BlocksPerPixel) || transform.BlocksPerPixel <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(transform));
        }

        if (!float.IsFinite(transform.ViewportSize.X)
            || !float.IsFinite(transform.ViewportSize.Y)
            || transform.ViewportSize.X <= 0f
            || transform.ViewportSize.Y <= 0f)
        {
            return default;
        }

        var tint = Math.Clamp(float.IsFinite(brightness) ? brightness : 1f, 0f, 1f);
        var topLeft = transform.ScreenToWorld(Vector2.Zero);
        var topRight = transform.ScreenToWorld(new Vector2(transform.ViewportSize.X, 0f));
        var bottomRight = transform.ScreenToWorld(transform.ViewportSize);
        var bottomLeft = transform.ScreenToWorld(new Vector2(0f, transform.ViewportSize.Y));
        var minimumX = Math.Max(
            (double)int.MinValue,
            Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomRight.X, bottomLeft.X)));
        var maximumX = Math.Min(
            (double)int.MaxValue,
            Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomRight.X, bottomLeft.X)));
        var minimumZ = Math.Max(
            (double)int.MinValue,
            Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomRight.Y, bottomLeft.Y)));
        var maximumZ = Math.Min(
            (double)int.MaxValue,
            Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomRight.Y, bottomLeft.Y)));
        if (minimumX > maximumX || minimumZ > maximumZ)
        {
            return default;
        }

        if (source is IExploredMapTileIndexSource indexedSource)
        {
            return RenderKnownTiles(
                indexedSource,
                transform,
                tint,
                sink,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ,
                useHeightShading);
        }

        var visibleStartX = (long)Math.Ceiling(minimumX);
        var visibleEndX = (long)Math.Floor(maximumX);
        var visibleStartZ = (long)Math.Ceiling(minimumZ);
        var visibleEndZ = (long)Math.Floor(maximumZ);
        if (visibleStartX > visibleEndX || visibleStartZ > visibleEndZ)
        {
            return default;
        }

        var stride = CalculateAlignedStride(
            visibleStartX,
            visibleEndX,
            visibleStartZ,
            visibleEndZ);
        var startXGroup = FloorDivide(visibleStartX, stride);
        var endXGroup = FloorDivide(visibleEndX, stride);
        var startZGroup = FloorDivide(visibleStartZ, stride);
        var endZGroup = FloorDivide(visibleEndZ, stride);
        var queries = 0;
        var primitives = 0;
        using var session = source.BeginReadSession();
        for (var zGroup = startZGroup; zGroup <= endZGroup; zGroup++)
        {
            var groupStartZ = checked(zGroup * stride);
            var clippedStartZ = Math.Max(groupStartZ, visibleStartZ);
            var clippedEndZ = Math.Min(groupStartZ + stride - 1L, visibleEndZ);
            var z = checked((int)clippedStartZ);
            var aggregateHeight = checked((int)(clippedEndZ - clippedStartZ + 1L));
            for (var xGroup = startXGroup; xGroup <= endXGroup; xGroup++)
            {
                var groupStartX = checked(xGroup * stride);
                var clippedStartX = Math.Max(groupStartX, visibleStartX);
                var clippedEndX = Math.Min(groupStartX + stride - 1L, visibleEndX);
                var x = checked((int)clippedStartX);
                var aggregateWidth = checked((int)(clippedEndX - clippedStartX + 1L));
                queries++;
                var renderWidth = 1;
                var renderHeight = 1;
                var found = stride > 1
                    && session is IExploredMapAggregateReadSession aggregateSession
                    ? aggregateSession.TryGetExploredTerrainRegion(
                        x,
                        z,
                        renderWidth = aggregateWidth,
                        renderHeight = aggregateHeight,
                        out var pixel)
                    : session.TryGetExploredTerrainPixel(x, z, out pixel);
                if (!found)
                {
                    continue;
                }

                var right = (float)((double)x + renderWidth);
                var bottom = (float)((double)z + renderHeight);
                sink.TerrainCell(new MapTerrainCell(
                    x,
                    z,
                    transform.WorldToScreen(new Vector2(x, z)),
                    transform.WorldToScreen(new Vector2(right, z)),
                    transform.WorldToScreen(new Vector2(right, bottom)),
                    transform.WorldToScreen(new Vector2(x, bottom)),
                    TerrainHeightShading.Apply(
                        pixel.Color,
                        useHeightShading ? pixel.HeightShade : TerrainHeightShading.Unknown,
                        tint)));
                primitives++;
            }
        }

        return new MapRenderStatistics(queries, primitives, stride);
    }

    public static void RenderOverlays(MapOverlayState state, ITravelMapRenderSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(state.Waypoints);
        sink.Player(
            state.PlayerPosition,
            state.PlayerHeading,
            Math.Clamp(state.PlayerArrowSize, 14f, 40f),
            state.PlayerMarkerColor ?? TravelMapPalette.SurveyCyan);

        foreach (var waypoint in state.Waypoints)
        {
            sink.Waypoint(waypoint, TravelMapPalette.HazardAmber);
        }

        if (state.LastDeath is { } lastDeath)
        {
            sink.LastDeath(lastDeath, TravelMapPalette.DeathMarkerBone);
        }

        if (state.ShowCoordinates)
        {
            sink.Label(
                FormatCoordinates(state.PlayerPosition),
                state.PlayerPosition,
                TravelMapPalette.SnowText);
        }
    }

    public static float PlayerArrowSize(int miniMapSize) => Math.Clamp(miniMapSize / 8f, 24f, 40f);

    public static float MiniMapPlayerArrowSize(int miniMapSize) =>
        Math.Clamp(miniMapSize * (3f / 32f), 14f, 24f);

    public static string FormatCoordinates(Vector3 position) => string.Format(
        CultureInfo.InvariantCulture,
        "X: {0}  Y: {1}  Z: {2}",
        (int)position.X,
        (int)position.Y,
        (int)position.Z);

    public static string FormatCompactCoordinates(Vector3 position) => string.Format(
        CultureInfo.InvariantCulture,
        "X:{0} Y:{1} Z:{2}",
        (int)position.X,
        (int)position.Y,
        (int)position.Z);

    private static int CalculateAlignedStride(
        long minimumX,
        long maximumX,
        long minimumZ,
        long maximumZ)
    {
        var stride = 1;
        while (true)
        {
            var columns = FloorDivide(maximumX, stride) - FloorDivide(minimumX, stride) + 1L;
            var rows = FloorDivide(maximumZ, stride) - FloorDivide(minimumZ, stride) + 1L;
            if (columns <= MaximumTerrainSamplesPerFrame / rows)
            {
                return stride;
            }

            stride = checked(stride * 2);
        }
    }

    private static long FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static MapRenderStatistics RenderKnownTiles(
        IExploredMapTileIndexSource source,
        MapTransform transform,
        float tint,
        ITravelMapRenderSink sink,
        double minimumX,
        double maximumX,
        double minimumZ,
        double maximumZ,
        bool useHeightShading)
    {
        var region = new MapTileRegion(
            TileCoordinate.FromWorld((int)Math.Floor(minimumX), 0).TileX,
            TileCoordinate.FromWorld((int)Math.Floor(maximumX), 0).TileX,
            TileCoordinate.FromWorld(0, (int)Math.Floor(minimumZ)).TileZ,
            TileCoordinate.FromWorld(0, (int)Math.Floor(maximumZ)).TileZ);
        var catalog = source is IBoundedExploredMapTileIndexSource boundedSource
            ? boundedSource.GetKnownTileCatalog(region, MaximumIndexedTileDescriptorsPerFrame)
            : CreateBoundedCatalog(source.GetKnownTiles(region));
        if (catalog.IsTruncated || catalog.Tiles.Count == 0)
        {
            return default;
        }

        var tiles = catalog.Tiles;

        var pixelStride = 1;
        while ((long)tiles.Count * SamplesPerWholeTile(pixelStride) > MaximumTerrainSamplesPerFrame)
        {
            pixelStride *= 2;
        }

        var sampleRanges = tiles
            .Select(tile => CreateSampleRange(
                tile,
                pixelStride,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ))
            .Where(range => range.SampleCount > 0)
            .ToArray();
        var queries = 0;
        var primitives = 0;
        using var session = source is IExploredMapLodSource lodSource
            ? lodSource.BeginLodReadSession(
                sampleRanges,
                pixelStride,
                TileStoreMapPixelSource.MaximumLodTileMaterializationsPerFrame)
            : source.BeginReadSession();
        foreach (var range in sampleRanges)
        {
            for (long worldZ = range.StartZ; worldZ <= range.EndZ; worldZ += pixelStride)
            {
                var z = (int)worldZ;
                for (long worldX = range.StartX; worldX <= range.EndX; worldX += pixelStride)
                {
                    var x = (int)worldX;
                    queries++;
                    var aggregateWidth = Math.Min(pixelStride, range.EndX - x + 1);
                    var aggregateHeight = Math.Min(pixelStride, range.EndZ - z + 1);
                    var renderWidth = 1;
                    var renderHeight = 1;
                    var found = pixelStride > 1
                        && session is IExploredMapAggregateReadSession aggregateSession
                        ? aggregateSession.TryGetExploredTerrainRegion(
                            x,
                            z,
                            renderWidth = aggregateWidth,
                            renderHeight = aggregateHeight,
                            out var pixel)
                        : session.TryGetExploredTerrainPixel(x, z, out pixel);
                    if (!found)
                    {
                        continue;
                    }

                    var right = (long)x + renderWidth > int.MaxValue ? int.MaxValue : x + renderWidth;
                    var bottom = (long)z + renderHeight > int.MaxValue ? int.MaxValue : z + renderHeight;
                    sink.TerrainCell(new MapTerrainCell(
                        x,
                        z,
                        transform.WorldToScreen(new Vector2(x, z)),
                        transform.WorldToScreen(new Vector2(right, z)),
                        transform.WorldToScreen(new Vector2(right, bottom)),
                        transform.WorldToScreen(new Vector2(x, bottom)),
                        TerrainHeightShading.Apply(
                            pixel.Color,
                            useHeightShading ? pixel.HeightShade : TerrainHeightShading.Unknown,
                            tint)));
                    primitives++;
                }
            }
        }

        return new MapRenderStatistics(queries, primitives, pixelStride);
    }

    private static MapTileCatalog CreateBoundedCatalog(IReadOnlyList<MapTileCoordinate> tiles) =>
        tiles.Count <= MaximumIndexedTileDescriptorsPerFrame
            ? new MapTileCatalog(tiles, IsTruncated: false)
            : new MapTileCatalog(
                tiles.Take(MaximumIndexedTileDescriptorsPerFrame).ToArray(),
                IsTruncated: true);

    private static long SamplesPerWholeTile(int stride)
    {
        var samplesPerAxis = ((MapTile.Size - 1) / stride) + 1;
        return (long)samplesPerAxis * samplesPerAxis;
    }

    private static MapTileSamplePlan CreateSampleRange(
        MapTileCoordinate tile,
        int stride,
        double minimumX,
        double maximumX,
        double minimumZ,
        double maximumZ)
    {
        var tileMinimumX = (long)tile.X * MapTile.Size;
        var tileMinimumZ = (long)tile.Z * MapTile.Size;
        var startX = Math.Max(tileMinimumX, (long)Math.Ceiling(minimumX));
        var endX = Math.Min(tileMinimumX + MapTile.Size - 1L, (long)Math.Floor(maximumX));
        var startZ = Math.Max(tileMinimumZ, (long)Math.Ceiling(minimumZ));
        var endZ = Math.Min(tileMinimumZ + MapTile.Size - 1L, (long)Math.Floor(maximumZ));
        if (startX > endX || startZ > endZ)
        {
            return default;
        }

        var columns = ((endX - startX) / stride) + 1;
        var rows = ((endZ - startZ) / stride) + 1;
        return new MapTileSamplePlan(
            tile,
            checked((int)startX),
            checked((int)endX),
            checked((int)startZ),
            checked((int)endZ),
            columns * rows);
    }
}

public readonly record struct MapRenderStatistics(int PixelQueries, int PrimitiveCount, int WorldStride)
{
    public int TerrainPrimitives => PrimitiveCount;
}
