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
}

public interface IExploredMapPixelSource
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
    bool ShowCoordinates);

public sealed class TileStoreMapPixelSource(ExplorationTileStore store) : IExploredMapPixelSource
{
    private readonly ExplorationTileStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
    {
        var coordinate = TileCoordinate.FromWorld(worldX, worldZ);
        return _store.GetOrLoad(coordinate.TileX, coordinate.TileZ)
            .TryGetPixel(coordinate.LocalX, coordinate.LocalZ, out color);
    }
}

public static class TravelMapRenderModel
{
    public static void RenderTerrain(
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

        var tint = Math.Clamp(float.IsFinite(brightness) ? brightness : 1f, 0f, 1f);
        var topLeft = transform.ScreenToWorld(Vector2.Zero);
        var bottomRight = transform.ScreenToWorld(transform.ViewportSize);
        var minimumX = checked((int)MathF.Floor(MathF.Min(topLeft.X, bottomRight.X)));
        var maximumX = checked((int)MathF.Ceiling(MathF.Max(topLeft.X, bottomRight.X)));
        var minimumZ = checked((int)MathF.Floor(MathF.Min(topLeft.Y, bottomRight.Y)));
        var maximumZ = checked((int)MathF.Ceiling(MathF.Max(topLeft.Y, bottomRight.Y)));

        for (var z = minimumZ; z <= maximumZ; z++)
        {
            for (var x = minimumX; x <= maximumX; x++)
            {
                if (!source.TryGetExploredPixel(x, z, out var color))
                {
                    continue;
                }

                var screenMinimum = transform.WorldToScreen(new Vector2(x, z));
                var screenMaximum = transform.WorldToScreen(new Vector2(x + 1f, z + 1f));
                sink.TerrainCell(new MapTerrainCell(
                    x,
                    z,
                    Vector2.Min(screenMinimum, screenMaximum),
                    Vector2.Max(screenMinimum, screenMaximum),
                    TintTerrain(color, tint)));
                EmitBoundaryEdges(source, transform, sink, x, z, screenMinimum, screenMaximum);
            }
        }
    }

    public static void RenderOverlays(MapOverlayState state, ITravelMapRenderSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(state.Waypoints);
        sink.Player(
            state.PlayerPosition,
            state.PlayerHeading,
            Math.Clamp(state.PlayerArrowSize, 24f, 40f),
            TravelMapPalette.SurveyCyan);

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

    public static string FormatCoordinates(Vector3 position) => string.Format(
        CultureInfo.InvariantCulture,
        "X: {0}  Y: {1}  Z: {2}",
        (int)position.X,
        (int)position.Y,
        (int)position.Z);

    private static Rgba32 TintTerrain(Rgba32 color, float brightness) => new(
        (byte)Math.Clamp((int)MathF.Round(color.R * brightness), 0, byte.MaxValue),
        (byte)Math.Clamp((int)MathF.Round(color.G * brightness), 0, byte.MaxValue),
        (byte)Math.Clamp((int)MathF.Round(color.B * brightness), 0, byte.MaxValue),
        color.A);

    private static void EmitBoundaryEdges(
        IExploredMapPixelSource source,
        MapTransform transform,
        ITravelMapRenderSink sink,
        int x,
        int z,
        Vector2 screenMinimum,
        Vector2 screenMaximum)
    {
        var minimum = Vector2.Min(screenMinimum, screenMaximum);
        var maximum = Vector2.Max(screenMinimum, screenMaximum);
        if (!source.TryGetExploredPixel(x, z - 1, out _))
        {
            sink.ExplorationBoundary(new MapBoundaryEdge(
                minimum,
                new Vector2(maximum.X, minimum.Y),
                TravelMapPalette.SurveyCyan));
        }

        if (!source.TryGetExploredPixel(x + 1, z, out _))
        {
            sink.ExplorationBoundary(new MapBoundaryEdge(
                new Vector2(maximum.X, minimum.Y),
                maximum,
                TravelMapPalette.SurveyCyan));
        }

        if (!source.TryGetExploredPixel(x, z + 1, out _))
        {
            sink.ExplorationBoundary(new MapBoundaryEdge(
                new Vector2(minimum.X, maximum.Y),
                maximum,
                TravelMapPalette.SurveyCyan));
        }

        if (!source.TryGetExploredPixel(x - 1, z, out _))
        {
            sink.ExplorationBoundary(new MapBoundaryEdge(
                minimum,
                new Vector2(minimum.X, maximum.Y),
                TravelMapPalette.SurveyCyan));
        }
    }
}
