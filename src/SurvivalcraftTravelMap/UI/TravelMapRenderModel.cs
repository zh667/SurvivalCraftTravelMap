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

    public static Rgba32 MiniMapCoordinateBackdrop { get; } = new(0x12, 0x12, 0x12, 0xA0);
}

public interface IExploredMapPixelSource
{
    IExploredMapReadSession BeginReadSession();
}

public interface IExploredMapReadSession : IDisposable
{
    bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color);
}

public interface ITravelMapRenderSink
{
    void TerrainCell(MapTerrainCell cell);

    void ExplorationBoundary(MapBoundaryEdge edge);

    void Player(Vector3 position, float heading, float size, Rgba32 color);

    void Waypoint(Waypoint waypoint, Rgba32 color);

    void Label(string text, Vector3 worldPosition, Rgba32 color);
}

public readonly record struct MapTerrainCell(
    int WorldX,
    int WorldZ,
    Vector2 ScreenMinimum,
    Vector2 ScreenMaximum,
    Rgba32 Color);

public readonly record struct MapBoundaryEdge(Vector2 Start, Vector2 End, Rgba32 Color);

public readonly record struct MapOverlayState(
    Vector3 PlayerPosition,
    float PlayerHeading,
    float PlayerArrowSize,
    IReadOnlyList<Waypoint> Waypoints,
    bool ShowCoordinates,
    Rgba32? PlayerMarkerColor)
{
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

    IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region, int maximumCount) => [];
}

public interface IExploredMapTileIndexSource : IExploredMapPixelSource
{
    IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region, int maximumCount);
}

public sealed class TileStoreMapPixelSource : IExploredMapTileIndexSource
{
    public const int MaximumTilesPerFrame = 128;

    private readonly object _sync = new();
    private readonly IMapTileProvider _provider;
    private readonly int _snapshotCapacity;
    private readonly Dictionary<(int X, int Z), SnapshotCacheEntry> _snapshotCache = [];
    private readonly LinkedList<(int X, int Z)> _snapshotLru = [];

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

    public IExploredMapReadSession BeginReadSession() => new ReadSession(this);

    public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region, int maximumCount) =>
        _provider.GetKnownTiles(region, maximumCount);

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

    private sealed class ReadSession(TileStoreMapPixelSource source) : IExploredMapReadSession
    {
        private readonly Dictionary<(int X, int Z), MapTileSnapshot> _tiles = [];
        private readonly HashSet<(int X, int Z)> _unknownTiles = [];

        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
        {
            var coordinate = TileCoordinate.FromWorld(worldX, worldZ);
            var key = (coordinate.TileX, coordinate.TileZ);
            if (_unknownTiles.Contains(key))
            {
                color = default;
                return false;
            }

            if (!_tiles.TryGetValue(key, out var snapshot))
            {
                if (!source._provider.IsKnownTile(key.TileX, key.TileZ))
                {
                    _unknownTiles.Add(key);
                    color = default;
                    return false;
                }

                snapshot = source.GetSnapshot(key.TileX, key.TileZ);
                _tiles.Add(key, snapshot);
            }

            return snapshot.TryGetPixel(coordinate.LocalX, coordinate.LocalZ, out color);
        }

        public void Dispose()
        {
            _tiles.Clear();
            _unknownTiles.Clear();
        }
    }

    private sealed class StoreTileProvider(ExplorationTileStore store) : IMapTileProvider
    {
        public ExplorationTileStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

        public MapTile GetOrLoad(int tileX, int tileZ) => Store.GetOrLoad(tileX, tileZ);

        public bool IsKnownTile(int tileX, int tileZ) => Store.ContainsKnownTile(tileX, tileZ);

        public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region, int maximumCount) =>
            Store.GetKnownTiles(region, maximumCount);
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

    public static MapRenderStatistics RenderTerrain(
        IExploredMapPixelSource source,
        MapTransform transform,
        float brightness,
        ITravelMapRenderSink sink)
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
        var halfWorldWidth = (double)transform.ViewportSize.X * transform.BlocksPerPixel / 2d;
        var halfWorldHeight = (double)transform.ViewportSize.Y * transform.BlocksPerPixel / 2d;
        var minimumX = Math.Max((double)int.MinValue, (double)transform.Center.X - halfWorldWidth);
        var maximumX = Math.Min((double)int.MaxValue, (double)transform.Center.X + halfWorldWidth);
        var minimumZ = Math.Max((double)int.MinValue, (double)transform.Center.Y - halfWorldHeight);
        var maximumZ = Math.Min((double)int.MaxValue, (double)transform.Center.Y + halfWorldHeight);
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
                maximumZ);
        }

        var stride = CalculateAlignedStride(minimumX, maximumX, minimumZ, maximumZ);
        var startX = (long)Math.Ceiling(minimumX / stride);
        var endX = (long)Math.Floor(maximumX / stride);
        var startZ = (long)Math.Ceiling(minimumZ / stride);
        var endZ = (long)Math.Floor(maximumZ / stride);
        if (startX > endX || startZ > endZ)
        {
            return default;
        }

        var columns = endX - startX + 1;
        var rows = endZ - startZ + 1;
        var totalSamples = columns * rows;
        var queries = 0;
        var primitives = 0;
        long processed = 0;
        using var session = source.BeginReadSession();
        for (var zIndex = startZ; zIndex <= endZ; zIndex++)
        {
            var z = checked((int)(zIndex * stride));
            for (var xIndex = startX; xIndex <= endX; xIndex++)
            {
                var x = checked((int)(xIndex * stride));
                processed++;
                queries++;
                if (!session.TryGetExploredPixel(x, z, out var color))
                {
                    continue;
                }

                var screenMinimum = transform.WorldToScreen(new Vector2(x, z));
                var screenMaximum = transform.WorldToScreen(new Vector2((double)x + 1d > int.MaxValue ? x : x + 1, (double)z + 1d > int.MaxValue ? z : z + 1));
                sink.TerrainCell(new MapTerrainCell(
                    x,
                    z,
                    Vector2.Min(screenMinimum, screenMaximum),
                    Vector2.Max(screenMinimum, screenMaximum),
                    TintTerrain(color, tint)));
                primitives++;
                if (stride == 1)
                {
                    EmitBoundaryEdges(
                        session,
                        sink,
                        x,
                        z,
                        screenMinimum,
                        screenMaximum,
                        totalSamples - processed,
                        ref queries,
                        ref primitives);
                }
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

    private static Rgba32 TintTerrain(Rgba32 color, float brightness) => new(
        (byte)Math.Clamp((int)MathF.Round(color.R * brightness), 0, byte.MaxValue),
        (byte)Math.Clamp((int)MathF.Round(color.G * brightness), 0, byte.MaxValue),
        (byte)Math.Clamp((int)MathF.Round(color.B * brightness), 0, byte.MaxValue),
        color.A);

    private static void EmitBoundaryEdges(
        IExploredMapReadSession source,
        ITravelMapRenderSink sink,
        int x,
        int z,
        Vector2 screenMinimum,
        Vector2 screenMaximum,
        long remainingTerrainSamples,
        ref int queries,
        ref int primitives)
    {
        var minimum = Vector2.Min(screenMinimum, screenMaximum);
        var maximum = Vector2.Max(screenMinimum, screenMaximum);
        TryEmitBoundary(source, sink, x, z, x, z > int.MinValue ? z - 1 : z, minimum, new Vector2(maximum.X, minimum.Y), remainingTerrainSamples, ref queries, ref primitives);
        TryEmitBoundary(source, sink, x, z, x < int.MaxValue ? x + 1 : x, z, new Vector2(maximum.X, minimum.Y), maximum, remainingTerrainSamples, ref queries, ref primitives);
        TryEmitBoundary(source, sink, x, z, x, z < int.MaxValue ? z + 1 : z, new Vector2(minimum.X, maximum.Y), maximum, remainingTerrainSamples, ref queries, ref primitives);
        TryEmitBoundary(source, sink, x, z, x > int.MinValue ? x - 1 : x, z, minimum, new Vector2(minimum.X, maximum.Y), remainingTerrainSamples, ref queries, ref primitives);
    }

    private static void TryEmitBoundary(
        IExploredMapReadSession source,
        ITravelMapRenderSink sink,
        int x,
        int z,
        int neighborX,
        int neighborZ,
        Vector2 start,
        Vector2 end,
        long remainingTerrainSamples,
        ref int queries,
        ref int primitives)
    {
        if (neighborX == x && neighborZ == z)
        {
            return;
        }

        if ((long)queries + remainingTerrainSamples >= MaximumTerrainSamplesPerFrame)
        {
            return;
        }

        queries++;
        if (!source.TryGetExploredPixel(neighborX, neighborZ, out _)
            && (long)primitives + remainingTerrainSamples < MaximumTerrainSamplesPerFrame)
        {
            sink.ExplorationBoundary(new MapBoundaryEdge(start, end, TravelMapPalette.SurveyCyan));
            primitives++;
        }
    }

    private static int CalculateAlignedStride(
        double minimumX,
        double maximumX,
        double minimumZ,
        double maximumZ)
    {
        var stride = 1;
        while (true)
        {
            var columns = Math.Floor(maximumX / stride) - Math.Ceiling(minimumX / stride) + 1d;
            var rows = Math.Floor(maximumZ / stride) - Math.Ceiling(minimumZ / stride) + 1d;
            if (columns <= 0d || rows <= 0d || columns * rows <= MaximumTerrainSamplesPerFrame)
            {
                return stride;
            }

            stride = checked(stride * 2);
        }
    }

    private static MapRenderStatistics RenderKnownTiles(
        IExploredMapTileIndexSource source,
        MapTransform transform,
        float tint,
        ITravelMapRenderSink sink,
        double minimumX,
        double maximumX,
        double minimumZ,
        double maximumZ)
    {
        var region = new MapTileRegion(
            TileCoordinate.FromWorld((int)Math.Floor(minimumX), 0).TileX,
            TileCoordinate.FromWorld((int)Math.Floor(maximumX), 0).TileX,
            TileCoordinate.FromWorld(0, (int)Math.Floor(minimumZ)).TileZ,
            TileCoordinate.FromWorld(0, (int)Math.Floor(maximumZ)).TileZ);
        var tiles = source.GetKnownTiles(region, TileStoreMapPixelSource.MaximumTilesPerFrame);
        if (tiles.Count == 0)
        {
            return default;
        }

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
        var totalSamples = sampleRanges.Sum(range => range.SampleCount);
        var processed = 0L;
        var queries = 0;
        var primitives = 0;
        using var session = source.BeginReadSession();
        foreach (var range in sampleRanges)
        {
            for (long worldZ = range.StartZ; worldZ <= range.EndZ; worldZ += pixelStride)
            {
                var z = (int)worldZ;
                for (long worldX = range.StartX; worldX <= range.EndX; worldX += pixelStride)
                {
                    var x = (int)worldX;
                    processed++;
                    queries++;
                    if (!session.TryGetExploredPixel(x, z, out var color))
                    {
                        continue;
                    }

                    var screenMinimum = transform.WorldToScreen(new Vector2(x, z));
                    var screenMaximum = transform.WorldToScreen(new Vector2(
                        x < int.MaxValue ? x + 1 : x,
                        z < int.MaxValue ? z + 1 : z));
                    sink.TerrainCell(new MapTerrainCell(
                        x,
                        z,
                        Vector2.Min(screenMinimum, screenMaximum),
                        Vector2.Max(screenMinimum, screenMaximum),
                        TintTerrain(color, tint)));
                    primitives++;
                    if (pixelStride == 1)
                    {
                        EmitBoundaryEdgesWithinTile(
                            session,
                            sink,
                            x,
                            z,
                            screenMinimum,
                            screenMaximum,
                            totalSamples - processed,
                            ref queries,
                            ref primitives);
                    }
                }
            }
        }

        return new MapRenderStatistics(queries, primitives, pixelStride);
    }

    private static long SamplesPerWholeTile(int stride)
    {
        var samplesPerAxis = ((MapTile.Size - 1) / stride) + 1;
        return (long)samplesPerAxis * samplesPerAxis;
    }

    private static void EmitBoundaryEdgesWithinTile(
        IExploredMapReadSession source,
        ITravelMapRenderSink sink,
        int x,
        int z,
        Vector2 screenMinimum,
        Vector2 screenMaximum,
        long remainingTerrainSamples,
        ref int queries,
        ref int primitives)
    {
        var coordinate = TileCoordinate.FromWorld(x, z);
        var minimum = Vector2.Min(screenMinimum, screenMaximum);
        var maximum = Vector2.Max(screenMinimum, screenMaximum);
        if (coordinate.LocalZ > 0)
        {
            TryEmitBoundary(source, sink, x, z, x, z - 1, minimum, new Vector2(maximum.X, minimum.Y), remainingTerrainSamples, ref queries, ref primitives);
        }

        if (coordinate.LocalX < MapTile.Size - 1)
        {
            TryEmitBoundary(source, sink, x, z, x + 1, z, new Vector2(maximum.X, minimum.Y), maximum, remainingTerrainSamples, ref queries, ref primitives);
        }

        if (coordinate.LocalZ < MapTile.Size - 1)
        {
            TryEmitBoundary(source, sink, x, z, x, z + 1, new Vector2(minimum.X, maximum.Y), maximum, remainingTerrainSamples, ref queries, ref primitives);
        }

        if (coordinate.LocalX > 0)
        {
            TryEmitBoundary(source, sink, x, z, x - 1, z, minimum, new Vector2(minimum.X, maximum.Y), remainingTerrainSamples, ref queries, ref primitives);
        }
    }

    private static TileSampleRange CreateSampleRange(
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
        startX = CeilingToStride(startX, stride);
        startZ = CeilingToStride(startZ, stride);
        if (startX > endX || startZ > endZ)
        {
            return default;
        }

        var columns = ((endX - startX) / stride) + 1;
        var rows = ((endZ - startZ) / stride) + 1;
        return new TileSampleRange(
            checked((int)startX),
            checked((int)endX),
            checked((int)startZ),
            checked((int)endZ),
            columns * rows);
    }

    private static long CeilingToStride(long value, int stride)
    {
        var remainder = value % stride;
        if (remainder == 0)
        {
            return value;
        }

        return value >= 0 ? value + stride - remainder : value - remainder;
    }

    private readonly record struct TileSampleRange(
        int StartX,
        int EndX,
        int StartZ,
        int EndZ,
        long SampleCount);
}

public readonly record struct MapRenderStatistics(int PixelQueries, int PrimitiveCount, int WorldStride)
{
    public int TerrainPrimitives => PrimitiveCount;
}
