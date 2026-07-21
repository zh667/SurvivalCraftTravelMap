using Engine;
using Engine.Graphics;
using Engine.Input;
using Engine.Media;
using Game;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.Waypoints;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace SurvivalcraftTravelMap.UI;

public readonly record struct PlayerMapPose(NVector3 Position, float Heading);

internal enum MapTextAlignment
{
    Default,
    BottomLeft,
    BottomCenter,
    BottomRight,
    Center,
}

internal interface IMapFontQueue
{
    void QueueText(
        string text,
        NVector2 position,
        Rgba32 color,
        MapTextAlignment alignment,
        float scale);
}

internal static class MiniMapTextRenderer
{
    public static void QueueWaypointLabel(
        IMapFontQueue queue,
        string text,
        NVector2 position,
        Rgba32 color,
        float scale = TravelMapTypography.SecondaryLabelScale) => queue.QueueText(
            text,
            position,
            color,
            MapTextAlignment.Default,
            scale);

    public static void QueueCoordinates(
        IMapFontQueue queue,
        string text,
        NVector2 position,
        Rgba32 color) => QueueCoordinates(
            queue,
            text,
            position,
            color,
            TravelMapTypography.SecondaryLabelScale);

    public static void QueueCoordinates(
        IMapFontQueue queue,
        string text,
        NVector2 position,
        Rgba32 color,
        float scale,
        MapTextAlignment alignment = MapTextAlignment.BottomLeft) => queue.QueueText(
            text,
            position,
            color,
            alignment,
            scale);

    public static void QueueCompassLabel(
        IMapFontQueue queue,
        string text,
        NVector2 position,
        Rgba32 color,
        float scale) => queue.QueueText(
            text,
            position,
            color,
            MapTextAlignment.Center,
            scale);
}

internal enum MapFramePrimitiveKind
{
    Shadow,
    Frame,
}

internal readonly record struct MapFramePrimitive(
    MapFramePrimitiveKind Kind,
    NVector2 Minimum,
    NVector2 Maximum,
    Rgba32 Color);

internal readonly record struct MapCoordinateBackdropPrimitive(
    NVector2 Minimum,
    NVector2 Maximum,
    Rgba32 Color);

internal readonly record struct MapPlayerPrimitive(
    NVector2 Tip,
    NVector2 Left,
    NVector2 Right,
    float Size,
    Rgba32 Color);

internal static class MiniMapVisualStyle
{
    public const float ShadowThickness = 1f;
    public const float FrameThickness = 2f;
    public const byte FrameShadowAlpha = 0x80;
    public const float CoordinateStripHeight = 18f;

    public static IReadOnlyList<MapFramePrimitive> CreateFramePrimitives(
        NVector2 size,
        bool showFrameShadow,
        float frameThickness,
        Rgba32 shadowColor,
        Rgba32 frameColor)
    {
        var primitives = new List<MapFramePrimitive>();
        var firstFrameInset = 1f;
        if (showFrameShadow)
        {
            primitives.Add(new MapFramePrimitive(
                MapFramePrimitiveKind.Shadow,
                new NVector2(0.5f),
                size - new NVector2(0.5f),
                shadowColor));
            firstFrameInset = 0.5f + ShadowThickness;
        }

        for (var index = 0; index < (int)MathF.Ceiling(frameThickness); index++)
        {
            var inset = firstFrameInset + index;
            primitives.Add(new MapFramePrimitive(
                MapFramePrimitiveKind.Frame,
                new NVector2(inset),
                size - new NVector2(inset),
                frameColor));
        }

        return primitives;
    }

    public static MapCoordinateBackdropPrimitive CreateCoordinateBackdrop(
        NVector2 size,
        float height = CoordinateStripHeight) => new(
        new NVector2(0f, MathF.Max(0f, size.Y - MathF.Max(0f, height))),
        size,
        TravelMapPalette.MiniMapCoordinateBackdrop);

    public static IReadOnlyList<MapPlayerPrimitive> CreatePlayerPrimitives(
        NVector2 center,
        float heading,
        float size,
        Rgba32 color,
        bool drawOutline)
    {
        var primitives = new List<MapPlayerPrimitive>(drawOutline ? 2 : 1);
        if (drawOutline)
        {
            primitives.Add(CreatePlayerPrimitive(
                center,
                heading,
                size + 3f,
                TravelMapPalette.MiniMapPlayerOutline));
        }

        primitives.Add(CreatePlayerPrimitive(center, heading, size, color));
        return primitives;
    }

    private static MapPlayerPrimitive CreatePlayerPrimitive(
        NVector2 center,
        float heading,
        float size,
        Rgba32 color)
    {
        var direction = new NVector2(MathF.Sin(heading), -MathF.Cos(heading));
        var side = new NVector2(-direction.Y, direction.X);
        return new MapPlayerPrimitive(
            center + (direction * (size * 0.55f)),
            center - (direction * (size * 0.35f)) + (side * (size * 0.32f)),
            center - (direction * (size * 0.35f)) - (side * (size * 0.32f)),
            size,
            color);
    }
}

internal enum MapSurfacePrimitiveKind
{
    Background,
    Terrain,
    ExplorationBoundary,
    Player,
    Waypoint,
    DeathMarker,
    Creature,
    CoordinateBackdrop,
    SurveyCrosshair,
    FrameShadow,
    Frame,
}

internal interface IMapSurfacePrimitiveQueue
{
    void QueueQuad(
        MapSurfacePrimitiveKind kind,
        NVector2 minimum,
        NVector2 maximum,
        Rgba32 color);

    void QueueQuad(
        MapSurfacePrimitiveKind kind,
        NVector2 point1,
        NVector2 point2,
        NVector2 point3,
        NVector2 point4,
        Rgba32 color);

    void QueueLine(
        MapSurfacePrimitiveKind kind,
        NVector2 start,
        NVector2 end,
        Rgba32 color);

    void QueueTriangle(MapSurfacePrimitiveKind kind, MapPlayerPrimitive primitive);

    void QueueTriangle(
        MapSurfacePrimitiveKind kind,
        NVector2 point1,
        NVector2 point2,
        NVector2 point3,
        Rgba32 color);

    void QueueDisc(
        MapSurfacePrimitiveKind kind,
        NVector2 center,
        float radius,
        Rgba32 color,
        int segments);

    void QueueRectangle(MapFramePrimitive primitive);
}

internal readonly record struct MapSurfaceDrawContext(
    NVector2 ViewportSize,
    IMapSurfacePrimitiveQueue PrimitiveQueue,
    IMapFontQueue? FontQueue = null);

internal static class MapSurfaceBatchGuard
{
    public const int MaximumAddressableVertices = ushort.MaxValue + 1;

    public static bool RequiresFlush(
        int triangleVertexCount,
        int additionalTriangleVertices,
        int lineVertexCount,
        int additionalLineVertices)
    {
        if (triangleVertexCount < 0
            || additionalTriangleVertices < 0
            || lineVertexCount < 0
            || additionalLineVertices < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(triangleVertexCount));
        }

        return additionalTriangleVertices > MaximumAddressableVertices
            || triangleVertexCount > MaximumAddressableVertices - additionalTriangleVertices
            || additionalLineVertices > MaximumAddressableVertices
            || lineVertexCount > MaximumAddressableVertices - additionalLineVertices;
    }
}

public class MapSurfaceWidget : Widget, ITravelMapRenderSink
{
    private readonly IExploredMapPixelSource _pixelSource;
    private readonly TravelMapSettings _settings;
    private readonly Func<PlayerMapPose> _playerPose;
    private readonly Func<IReadOnlyList<Waypoint>> _waypoints;
    private readonly Func<IReadOnlyList<CreatureMapMarker>> _creatures;
    private readonly Func<DeathMapMarker?> _lastDeath;
    private Func<DeathMapMarker?> _previousDeath = static () => null;
    private readonly Func<float> _brightness;
    private readonly BitmapFont _font;
    private IMapSurfacePrimitiveQueue? _primitiveQueue;
    private IMapSurfacePrimitiveQueue? _frameQueue;
    private IMapFontQueue? _mapFontQueue;
    private MapShapeGeometry? _shapeGeometry;
    private Engine.Matrix _drawTransform;
    private MapTransform _drawMapTransform;
    private NVector2? _labelPointer;
    private IReadOnlyList<Waypoint> _drawWaypoints = Array.Empty<Waypoint>();
    private (int X, int Y, int Z) _lastCoordinate;
    private string _coordinateText = string.Empty;
    private int _lastGameMinute = -1;
    private string _gameTimeText = string.Empty;

    public MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<float> brightness)
        : this(pixelSource, settings, playerPose, waypoints, () => [], () => null, brightness, font: null)
    {
    }

    public MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<float> brightness)
        : this(pixelSource, settings, playerPose, waypoints, creatures, () => null, brightness, font: null)
    {
    }

    public MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<DeathMapMarker?> lastDeath,
        Func<float> brightness)
        : this(pixelSource, settings, playerPose, waypoints, creatures, lastDeath, brightness, font: null)
    {
    }

    internal MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<float> brightness,
        BitmapFont? font)
        : this(pixelSource, settings, playerPose, waypoints, () => [], () => null, brightness, font)
    {
    }

    internal MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<float> brightness,
        BitmapFont? font)
        : this(pixelSource, settings, playerPose, waypoints, creatures, () => null, brightness, font)
    {
    }

    internal MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<DeathMapMarker?> lastDeath,
        Func<float> brightness,
        BitmapFont? font)
    {
        _pixelSource = pixelSource ?? throw new ArgumentNullException(nameof(pixelSource));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _playerPose = playerPose ?? throw new ArgumentNullException(nameof(playerPose));
        _waypoints = waypoints ?? throw new ArgumentNullException(nameof(waypoints));
        _creatures = creatures ?? throw new ArgumentNullException(nameof(creatures));
        _lastDeath = lastDeath ?? throw new ArgumentNullException(nameof(lastDeath));
        _brightness = brightness ?? throw new ArgumentNullException(nameof(brightness));
        _font = font ?? ContentManager.Get<BitmapFont>("Fonts/Pericles");
        IsDrawRequired = true;
        ClampToBounds = true;
    }

    public bool AutoCenterOnPlayer { get; set; }

    public bool ApplyConfiguredMiniMapOrientation { get; set; }

    public bool ApplyConfiguredMiniMapShape { get; set; }

    public bool ShowCompassOverlay { get; set; }

    public bool ShowWaypointLabels { get; set; }

    public bool ShowSurveyCrosshair { get; set; } = true;

    public bool ShowFrameShadow { get; set; }

    public bool ShowCoordinateBackdrop { get; set; }

    public Func<float> GameTimeProvider { get; set; } = static () => 0f;

    public bool ShowMapInformation { get; set; } = true;

    public bool PlaceMapInformationBelowSurface { get; set; }

    public bool UseCompactCoordinates { get; set; }

    public bool DrawPlayerOutline { get; set; }

    public float CoordinateTextScale { get; set; } = TravelMapTypography.SecondaryLabelScale;

    public float FrameThickness { get; set; } = 1f;

    public Rgba32 PlayerMarkerColor { get; set; } = TravelMapPalette.SurveyCyan;

    public Rgba32 BackgroundColor { get; set; } = TravelMapPalette.Basalt;

    public Rgba32 FrameColor { get; set; } = TravelMapPalette.Moss;

    public Rgba32 FrameShadowColor { get; set; } = new(
        0x12,
        0x12,
        0x12,
        MiniMapVisualStyle.FrameShadowAlpha);

    public float PlayerArrowSize { get; set; } = 32f;

    public float DeathMarkerSize { get; set; } = 16f;

    /// <summary>
    /// Supplies the second-to-last death. Drawn as a distinct, dimmed, untracked marker: it is only
    /// rendered and hit-testable while its true position is on-screen (never edge-clamped).
    /// </summary>
    public Func<DeathMapMarker?> PreviousDeathProvider
    {
        get => _previousDeath;
        set => _previousDeath = value ?? (static () => null);
    }

    public NVector2? LabelPointer
    {
        get => _labelPointer;
        set => _labelPointer = value;
    }

    public MapTransform Transform { get; set; } = new(NVector2.Zero, 1f, NVector2.One);

    public bool IsExplored(NVector2 worldPosition)
    {
        if (!float.IsFinite(worldPosition.X)
            || !float.IsFinite(worldPosition.Y)
            || worldPosition.X < int.MinValue
            || worldPosition.X > int.MaxValue
            || worldPosition.Y < int.MinValue
            || worldPosition.Y > int.MaxValue)
        {
            return false;
        }

        using var session = _pixelSource.BeginReadSession();
        return session.TryGetExploredPixel(
            (int)MathF.Floor(worldPosition.X),
            (int)MathF.Floor(worldPosition.Y),
            out _);
    }

    public Waypoint? HitWaypoint(NVector2 localPosition, float hitRadius = 12f)
    {
        if (!ContainsLocalPoint(localPosition))
        {
            return null;
        }

        var waypoints = _waypoints();
        Waypoint? nearest = null;
        var nearestDistanceSquared = hitRadius * hitRadius;
        foreach (var waypoint in waypoints)
        {
            var screen = Transform.WorldToScreen(new NVector2(waypoint.Position.X, waypoint.Position.Z));
            var distanceSquared = NVector2.DistanceSquared(localPosition, screen);
            if (distanceSquared <= nearestDistanceSquared)
            {
                nearest = waypoint;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return nearest;
    }

    public DeathMapMarker? HitLastDeath(NVector2 localPosition, float hitRadius = 16f)
    {
        if (!_settings.ShowLastDeathMarker || !ContainsLocalPoint(localPosition))
        {
            return null;
        }

        var marker = _lastDeath();
        if (marker is null)
        {
            return null;
        }

        var projection = ProjectLastDeath(marker, hitRadius);
        return NVector2.DistanceSquared(localPosition, projection.Position) <= hitRadius * hitRadius
            ? marker
            : null;
    }

    public DeathMapMarker? HitPreviousDeath(NVector2 localPosition, float hitRadius = 16f)
    {
        if (!_settings.ShowLastDeathMarker || !ContainsLocalPoint(localPosition))
        {
            return null;
        }

        var marker = _previousDeath();
        if (marker is null)
        {
            return null;
        }

        var projection = ProjectLastDeath(marker, hitRadius);

        // The previous-death marker is untracked: it is never edge-clamped, so it can only be
        // interacted with while its true position is on-screen.
        if (projection.IsOffscreen)
        {
            return null;
        }

        return NVector2.DistanceSquared(localPosition, projection.Position) <= hitRadius * hitRadius
            ? marker
            : null;
    }

    internal DeathMarkerProjection ProjectLastDeath(DeathMapMarker marker, float inset = 16f) =>
        DeathMarkerTracking.Project(
            Transform,
            GetSurfaceViewportSize(),
            EffectiveMapShape,
            new NVector2(marker.Position.X, marker.Position.Z),
            inset);

    public bool ContainsLocalPoint(NVector2 localPosition) =>
        MapShapeGeometry.Create(
            GetSurfaceViewportSize(),
            EffectiveMapShape).ContainsPoint(localPosition);

    public override void MeasureOverride(Engine.Vector2 parentAvailableSize)
    {
        DesiredSize = new Engine.Vector2(float.PositiveInfinity);
        IsDrawRequired = true;
    }

    public override void Draw(DrawContext dc)
    {
        var viewport = GetSurfaceViewportSize();
        if (viewport.X <= 0f || viewport.Y <= 0f)
        {
            return;
        }

        _drawTransform = GlobalTransform;
        var flatBatch = dc.PrimitivesRenderer2D.FlatBatch(
            0,
            depthStencilState: DepthStencilState.None,
            blendState: BlendState.AlphaBlend);
        var fontBatch = dc.PrimitivesRenderer2D.FontBatch(
            _font,
            1,
            depthStencilState: DepthStencilState.None,
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.PointClamp);
        var textStart = fontBatch.TriangleVertices.Count;
        var primitiveQueue = new EngineMapSurfacePrimitiveQueue(flatBatch, _drawTransform);

        Draw(new MapSurfaceDrawContext(
            viewport,
            primitiveQueue,
            new EngineMapFontQueue(fontBatch)));

        primitiveQueue.Complete();
        fontBatch.TransformTriangles(_drawTransform, textStart);
    }

    internal void Draw(MapSurfaceDrawContext context)
    {
        var viewport = context.ViewportSize;
        if (viewport.X <= 0f || viewport.Y <= 0f)
        {
            return;
        }

        var pose = _playerPose();
        var center = AutoCenterOnPlayer
            ? new NVector2(pose.Position.X, pose.Position.Z)
            : Transform.Center;
        var rotation = ApplyConfiguredMiniMapOrientation
            && _settings.MiniMapOrientation == MiniMapOrientation.HeadingUp
                ? -pose.Heading
                : 0f;
        Transform = Transform with
        {
            Center = center,
            ViewportSize = viewport,
            RotationRadians = rotation,
        };
        _drawMapTransform = Transform;
        _frameQueue = context.PrimitiveQueue
            ?? throw new ArgumentNullException(nameof(context));
        _shapeGeometry = MapShapeGeometry.Create(viewport, EffectiveMapShape);
        _primitiveQueue = new ShapeClippedPrimitiveQueue(_frameQueue, _shapeGeometry);
        _mapFontQueue = context.FontQueue;
        _primitiveQueue.QueueQuad(
            MapSurfacePrimitiveKind.Background,
            NVector2.Zero,
            viewport,
            new Rgba32(BackgroundColor.R, BackgroundColor.G, BackgroundColor.B, 224));

        var terrainBrightness = _settings.UseDayNightTint ? _brightness() : 1f;
        TravelMapRenderModel.RenderTerrain(
            _pixelSource,
            Transform,
            terrainBrightness,
            this,
            _settings.HeightShadingStyle.ToStrength());
        _drawWaypoints = _waypoints();
        DrawCreatureMarkers();
        TravelMapRenderModel.RenderOverlays(
            new MapOverlayState(
                pose.Position,
                pose.Heading,
                PlayerArrowSize,
                _drawWaypoints,
                _settings.ShowCoordinates,
                PlayerMarkerColor)
            {
                LastDeath = _settings.ShowLastDeathMarker ? _lastDeath() : null,
                PreviousDeath = _settings.ShowLastDeathMarker ? _previousDeath() : null,
            },
            this);
        DrawBottomOverlayBackdrop();
        DrawGameTime();
        DrawCompass();
        if (ShowSurveyCrosshair)
        {
            QueueSurveyCrosshair(Transform.WorldToScreen(new NVector2(pose.Position.X, pose.Position.Z)));
        }

        QueueShapeFrame(viewport);

        _primitiveQueue = null;
        _frameQueue = null;
        _mapFontQueue = null;
        _shapeGeometry = null;
        _drawWaypoints = Array.Empty<Waypoint>();
    }

    public void TerrainCell(MapTerrainCell cell)
    {
        _primitiveQueue!.QueueQuad(
            MapSurfacePrimitiveKind.Terrain,
            cell.ScreenTopLeft,
            cell.ScreenTopRight,
            cell.ScreenBottomRight,
            cell.ScreenBottomLeft,
            cell.Color);
    }

    public void ExplorationBoundary(MapBoundaryEdge edge)
    {
        _primitiveQueue!.QueueLine(
            MapSurfacePrimitiveKind.ExplorationBoundary,
            edge.Start,
            edge.End,
            edge.Color);
    }

    // Marker labels are anchored to world positions, so a fixed screen font looks oversized once
    // the large map is zoomed far out (many blocks per pixel). Scale the text with the zoom so it
    // tracks the map: shrink as BlocksPerPixel grows, with a floor that keeps it readable and a cap
    // so it never grows past the normal (minimap) size.
    private float ZoomAwareLabelScale()
    {
        const float referenceBlocksPerPixel = 2f;
        var zoomFactor = Math.Clamp(referenceBlocksPerPixel / _drawMapTransform.BlocksPerPixel, 0.4f, 1f);
        return TravelMapTypography.SecondaryLabelScale * zoomFactor;
    }

    public void Player(NVector3 position, float heading, float size, Rgba32 color)
    {
        var center = _drawMapTransform.WorldToScreen(new NVector2(position.X, position.Z));
        foreach (var primitive in MiniMapVisualStyle.CreatePlayerPrimitives(
                     center,
                     heading + _drawMapTransform.RotationRadians,
                     size,
                     color,
                     DrawPlayerOutline))
        {
            _primitiveQueue!.QueueTriangle(MapSurfacePrimitiveKind.Player, primitive);
        }
    }

    public void Waypoint(Waypoint waypoint, Rgba32 color)
    {
        var center = _drawMapTransform.WorldToScreen(new NVector2(waypoint.Position.X, waypoint.Position.Z));
        if (!IsInside(center, 12f))
        {
            return;
        }

        const float radius = 6f;
        _primitiveQueue!.QueueQuad(
            MapSurfacePrimitiveKind.Waypoint,
            center + new NVector2(0f, -radius),
            center + new NVector2(radius, 0f),
            center + new NVector2(0f, radius),
            center + new NVector2(-radius, 0f),
            color);
        if (ShowWaypointLabels && ShouldDrawWaypointLabel(waypoint, center))
        {
            MiniMapTextRenderer.QueueWaypointLabel(
                _mapFontQueue!,
                waypoint.Name,
                center + new NVector2(9f, -9f),
                TravelMapPalette.SnowText,
                ZoomAwareLabelScale());
        }
    }

    public void LastDeath(DeathMapMarker marker, Rgba32 color) =>
        DrawDeathSkull(marker, color, tracked: true);

    public void PreviousDeath(DeathMapMarker marker, Rgba32 color) =>
        DrawDeathSkull(marker, color, tracked: false);

    private void DrawDeathSkull(DeathMapMarker marker, Rgba32 color, bool tracked)
    {
        var radius = Math.Clamp(DeathMarkerSize * 0.38f, 4.5f, 7f);
        var projection = DeathMarkerTracking.Project(
            _drawMapTransform,
            GetSurfaceViewportSize(),
            EffectiveMapShape,
            new NVector2(marker.Position.X, marker.Position.Z),
            radius + 6f);

        // The previous-death marker is untracked: never edge-clamped and only drawn on-screen.
        if (!tracked && projection.IsOffscreen)
        {
            return;
        }

        var center = projection.Position;

        var queue = _primitiveQueue!;
        var outline = TravelMapPalette.DeathMarkerOutline;
        var bone = color;

        if (tracked && projection.IsOffscreen)
        {
            var side = new NVector2(-projection.Direction.Y, projection.Direction.X);
            var tip = center + (projection.Direction * (radius + 5f));
            queue.QueueTriangle(
                MapSurfacePrimitiveKind.DeathMarker,
                tip,
                center - (projection.Direction * 2f) + (side * 4f),
                center - (projection.Direction * 2f) - (side * 4f),
                outline);
        }

        // Crossbones sit behind the head and keep the symbol recognizable at minimap scale.
        queue.QueueLine(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(-radius - 3f, -radius - 3f),
            center + new NVector2(radius + 3f, radius + 3f),
            outline);
        queue.QueueLine(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(radius + 3f, -radius - 3f),
            center + new NVector2(-radius - 3f, radius + 3f),
            outline);
        queue.QueueDisc(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(0f, -1.5f),
            radius + 1.5f,
            outline,
            segments: 16);
        queue.QueueDisc(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(0f, -1.5f),
            radius,
            bone,
            segments: 16);
        queue.QueueQuad(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(-radius * 0.55f, radius * 0.3f),
            center + new NVector2(radius * 0.55f, radius + 2f),
            bone);
        queue.QueueDisc(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(-radius * 0.38f, -radius * 0.2f),
            MathF.Max(1.3f, radius * 0.22f),
            outline,
            segments: 8);
        queue.QueueDisc(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(radius * 0.38f, -radius * 0.2f),
            MathF.Max(1.3f, radius * 0.22f),
            outline,
            segments: 8);
        queue.QueueTriangle(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(0f, radius * 0.05f),
            center + new NVector2(-1.3f, radius * 0.32f),
            center + new NVector2(1.3f, radius * 0.32f),
            outline);
        queue.QueueLine(
            MapSurfacePrimitiveKind.DeathMarker,
            center + new NVector2(0f, radius * 0.42f),
            center + new NVector2(0f, radius + 1.5f),
            outline);

        if (tracked && ShowWaypointLabels && _mapFontQueue is not null)
        {
            MiniMapTextRenderer.QueueWaypointLabel(
                _mapFontQueue,
                TravelMapText.Get("lastDeathLocation", "上次死亡地点"),
                center + new NVector2(radius + 6f, -radius - 3f),
                TravelMapPalette.SnowText,
                ZoomAwareLabelScale());
        }
    }

    private void DrawCreatureMarkers()
    {
        if (!_settings.ShowCreatureMarkers)
        {
            return;
        }

        var markerSize = _settings.CreatureMarkerSize;
        var fillRadius = markerSize * 0.5f;
        var outlineRadius = fillRadius + MathF.Max(1.5f, markerSize * 0.1f);
        foreach (var creature in _creatures())
        {
            var center = _drawMapTransform.WorldToScreen(
                new NVector2(creature.Position.X, creature.Position.Z));
            if (!IsInsideViewport(center, outlineRadius))
            {
                continue;
            }

            _primitiveQueue!.QueueDisc(
                MapSurfacePrimitiveKind.Creature,
                center,
                outlineRadius,
                CreatureMapMarkerStyle.OutlineColor,
                segments: 12);
            _primitiveQueue.QueueDisc(
                MapSurfacePrimitiveKind.Creature,
                center,
                fillRadius,
                CreatureMapMarkerStyle.ColorFor(creature.Kind),
                segments: 12);
        }
    }

    public void Label(string text, NVector3 worldPosition, Rgba32 color)
    {
        if (!ShowMapInformation || !_settings.ShowCoordinates)
        {
            return;
        }

        var coordinate = ((int)worldPosition.X, (int)worldPosition.Y, (int)worldPosition.Z);
        if (coordinate != _lastCoordinate || _coordinateText.Length == 0)
        {
            _lastCoordinate = coordinate;
            _coordinateText = UseCompactCoordinates
                ? TravelMapRenderModel.FormatCompactCoordinates(worldPosition)
                : TravelMapRenderModel.FormatCoordinates(worldPosition);
        }

        var viewport = _drawMapTransform.ViewportSize;
        if (PlaceMapInformationBelowSurface)
        {
            MiniMapTextRenderer.QueueCoordinates(
                _mapFontQueue!,
                _coordinateText,
                new NVector2(8f, viewport.Y + MiniMapRenderer.InformationSecondLineBaseline),
                color,
                CoordinateTextScale,
                MapTextAlignment.BottomLeft);
            return;
        }

        var centered = EffectiveMapShape == MapShape.Circle;
        var coordinatePosition = centered
            ? new NVector2(viewport.X / 2f, viewport.Y * 0.78f)
            : new NVector2(10f, viewport.Y - 12f);
        var coordinateScale = CoordinateTextScale;
        if (centered && _shapeGeometry is not null)
        {
            var availableWidth = MathF.Max(1f, _shapeGeometry.HorizontalSpanAt(coordinatePosition.Y - 6f) - 12f);
            var estimatedWidth = MathF.Max(1f, _coordinateText.Length * 8f * coordinateScale);
            coordinateScale *= MathF.Min(1f, availableWidth / estimatedWidth);
        }

        MiniMapTextRenderer.QueueCoordinates(
            _mapFontQueue!,
            _coordinateText,
            coordinatePosition,
            color,
            coordinateScale,
            centered ? MapTextAlignment.BottomCenter : MapTextAlignment.BottomLeft);
    }

    private bool ShouldDrawWaypointLabel(Waypoint waypoint, NVector2 position)
    {
        var labelAnchor = position + new NVector2(9f, -9f);
        var estimatedWidth = MathF.Max(24f, waypoint.Name.Length * 12f * TravelMapTypography.SecondaryLabelScale);
        const float estimatedHeight = 18f;
        if (_shapeGeometry is null
            || !_shapeGeometry.ContainsPoint(labelAnchor)
            || !_shapeGeometry.ContainsPoint(labelAnchor + new NVector2(estimatedWidth, 0f))
            || !_shapeGeometry.ContainsPoint(labelAnchor + new NVector2(0f, estimatedHeight))
            || !_shapeGeometry.ContainsPoint(labelAnchor + new NVector2(estimatedWidth, estimatedHeight)))
        {
            return false;
        }

        var pointer = _labelPointer ?? (_drawMapTransform.ViewportSize / 2f);
        var pointerDistance = NVector2.DistanceSquared(position, pointer);
        foreach (var other in _drawWaypoints)
        {
            if (other.Id == waypoint.Id)
            {
                continue;
            }

            var otherPosition = _drawMapTransform.WorldToScreen(new NVector2(other.Position.X, other.Position.Z));
            if (MathF.Abs(otherPosition.X - position.X) > 96f || MathF.Abs(otherPosition.Y - position.Y) > 22f)
            {
                continue;
            }

            if (NVector2.DistanceSquared(otherPosition, pointer) < pointerDistance)
            {
                return false;
            }
        }

        return true;
    }

    private void DrawCompass()
    {
        if (!ShowCompassOverlay || _mapFontQueue is null)
        {
            return;
        }

        var bottomReservedHeight = GetBottomOverlayHeight();
        foreach (var label in CompassLayout.Create(
                     _drawMapTransform.ViewportSize,
                     _drawMapTransform.RotationRadians,
                     EffectiveMapShape,
                     _settings.ShowCompassNorth,
                     _settings.ShowCompassOtherDirections,
                     _settings.CompassFontScale,
                     bottomReservedHeight))
        {
            MiniMapTextRenderer.QueueCompassLabel(
                _mapFontQueue,
                TravelMapText.CompassDirection(label.Direction),
                label.Position,
                label.IsNorth ? TravelMapPalette.CompassNorth : TravelMapPalette.SnowText,
                _settings.CompassFontScale);
        }
    }

    private sealed class EngineMapFontQueue(FontBatch2D batch) : IMapFontQueue
    {
        public void QueueText(
            string text,
            NVector2 position,
            Rgba32 color,
            MapTextAlignment alignment,
            float scale) => batch.QueueText(
                text,
                ToEngine(position),
                0f,
                ToEngineColor(color),
                alignment switch
                {
                    MapTextAlignment.BottomLeft => TextAnchor.Bottom | TextAnchor.Left,
                    MapTextAlignment.BottomCenter => TextAnchor.Bottom | TextAnchor.HorizontalCenter,
                    MapTextAlignment.BottomRight => TextAnchor.Bottom | TextAnchor.Right,
                    MapTextAlignment.Center => TextAnchor.HorizontalCenter | TextAnchor.VerticalCenter,
                    _ => TextAnchor.Default,
                },
                new Engine.Vector2(scale),
                Engine.Vector2.Zero);
    }

    private sealed class EngineMapSurfacePrimitiveQueue(
        FlatBatch2D batch,
        Engine.Matrix transform) : IMapSurfacePrimitiveQueue
    {
        private int _triangleStart = batch.TriangleVertices.Count;
        private int _lineStart = batch.LineVertices.Count;
        private bool _completed;

        public void QueueQuad(
            MapSurfacePrimitiveKind kind,
            NVector2 minimum,
            NVector2 maximum,
            Rgba32 color)
        {
            EnsureCapacity(additionalTriangleVertices: 4, additionalLineVertices: 0);
            batch.QueueQuad(
                ToEngine(minimum),
                ToEngine(maximum),
                0f,
                ToEngineColor(color));
        }

        public void QueueQuad(
            MapSurfacePrimitiveKind kind,
            NVector2 point1,
            NVector2 point2,
            NVector2 point3,
            NVector2 point4,
            Rgba32 color)
        {
            EnsureCapacity(additionalTriangleVertices: 4, additionalLineVertices: 0);
            batch.QueueQuad(
                ToEngine(point1),
                ToEngine(point2),
                ToEngine(point3),
                ToEngine(point4),
                0f,
                ToEngineColor(color));
        }

        public void QueueLine(
            MapSurfacePrimitiveKind kind,
            NVector2 start,
            NVector2 end,
            Rgba32 color)
        {
            EnsureCapacity(additionalTriangleVertices: 0, additionalLineVertices: 2);
            batch.QueueLine(
                ToEngine(start),
                ToEngine(end),
                0f,
                ToEngineColor(color));
        }

        public void QueueTriangle(
            MapSurfacePrimitiveKind kind,
            MapPlayerPrimitive primitive)
        {
            EnsureCapacity(additionalTriangleVertices: 3, additionalLineVertices: 0);
            batch.QueueTriangle(
                ToEngine(primitive.Tip),
                ToEngine(primitive.Left),
                ToEngine(primitive.Right),
                0f,
                ToEngineColor(primitive.Color));
        }

        public void QueueTriangle(
            MapSurfacePrimitiveKind kind,
            NVector2 point1,
            NVector2 point2,
            NVector2 point3,
            Rgba32 color)
        {
            EnsureCapacity(additionalTriangleVertices: 3, additionalLineVertices: 0);
            batch.QueueTriangle(
                ToEngine(point1),
                ToEngine(point2),
                ToEngine(point3),
                0f,
                ToEngineColor(color));
        }

        public void QueueDisc(
            MapSurfacePrimitiveKind kind,
            NVector2 center,
            float radius,
            Rgba32 color,
            int segments)
        {
            var count = Math.Max(3, segments);
            EnsureCapacity(additionalTriangleVertices: checked(count * 3), additionalLineVertices: 0);
            batch.QueueDisc(
                ToEngine(center),
                new Engine.Vector2(radius),
                0f,
                ToEngineColor(color),
                count,
                0f,
                MathF.Tau);
        }

        public void QueueRectangle(MapFramePrimitive primitive)
        {
            EnsureCapacity(additionalTriangleVertices: 0, additionalLineVertices: 4);
            batch.QueueRectangle(
                ToEngine(primitive.Minimum),
                ToEngine(primitive.Maximum),
                0f,
                ToEngineColor(primitive.Color));
        }

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            TransformPendingVertices();
            _completed = true;
        }

        private void EnsureCapacity(int additionalTriangleVertices, int additionalLineVertices)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The map primitive queue has already completed.");
            }

            if (!MapSurfaceBatchGuard.RequiresFlush(
                    batch.TriangleVertices.Count,
                    additionalTriangleVertices,
                    batch.LineVertices.Count,
                    additionalLineVertices))
            {
                return;
            }

            // FlatBatch2D casts vertex indices to ushort. Flush the current
            // shared batch before an index can wrap and corrupt earlier quads.
            TransformPendingVertices();
            batch.Flush(PrimitivesRenderer2D.ViewportMatrix(), clearAfterFlush: true);
            _triangleStart = 0;
            _lineStart = 0;
        }

        private void TransformPendingVertices()
        {
            batch.TransformTriangles(transform, _triangleStart);
            batch.TransformLines(transform, _lineStart);
            _triangleStart = batch.TriangleVertices.Count;
            _lineStart = batch.LineVertices.Count;
        }
    }

    private void QueueSurveyCrosshair(NVector2 center)
    {
        if (!IsInside(center, 24f))
        {
            return;
        }

        _primitiveQueue!.QueueLine(
            MapSurfacePrimitiveKind.SurveyCrosshair,
            center + new NVector2(-22f, 0f),
            center + new NVector2(-9f, 0f),
            TravelMapPalette.SurveyCyan);
        _primitiveQueue.QueueLine(
            MapSurfacePrimitiveKind.SurveyCrosshair,
            center + new NVector2(9f, 0f),
            center + new NVector2(22f, 0f),
            TravelMapPalette.SurveyCyan);
        _primitiveQueue.QueueLine(
            MapSurfacePrimitiveKind.SurveyCrosshair,
            center + new NVector2(0f, -22f),
            center + new NVector2(0f, -9f),
            TravelMapPalette.SurveyCyan);
        _primitiveQueue.QueueLine(
            MapSurfacePrimitiveKind.SurveyCrosshair,
            center + new NVector2(0f, 9f),
            center + new NVector2(0f, 22f),
            TravelMapPalette.SurveyCyan);
    }

    private void DrawBottomOverlayBackdrop()
    {
        var height = GetBottomOverlayHeight();
        if (!ShowCoordinateBackdrop || height <= 0f)
        {
            return;
        }

        var backdrop = MiniMapVisualStyle.CreateCoordinateBackdrop(
            _drawMapTransform.ViewportSize,
            height);
        _primitiveQueue!.QueueQuad(
            MapSurfacePrimitiveKind.CoordinateBackdrop,
            backdrop.Minimum,
            backdrop.Maximum,
            backdrop.Color);
    }

    private void DrawGameTime()
    {
        if (!ShowMapInformation || !_settings.ShowGameTime || _mapFontQueue is null)
        {
            return;
        }

        var minute = GameTimeFormatter.GetDisplayedMinute(GameTimeProvider());
        if (minute != _lastGameMinute || _gameTimeText.Length == 0)
        {
            _lastGameMinute = minute;
            _gameTimeText = GameTimeFormatter.FormatMinute(minute);
        }

        var viewport = _drawMapTransform.ViewportSize;
        if (PlaceMapInformationBelowSurface)
        {
            _mapFontQueue.QueueText(
                _gameTimeText,
                new NVector2(8f, viewport.Y + MiniMapRenderer.InformationFirstLineBaseline),
                TravelMapPalette.SnowText,
                MapTextAlignment.BottomLeft,
                CoordinateTextScale);
            return;
        }

        var centered = EffectiveMapShape == MapShape.Circle;
        var hasCoordinates = _settings.ShowCoordinates;
        var position = centered
            ? new NVector2(viewport.X / 2f, viewport.Y * (hasCoordinates ? 0.66f : 0.78f))
            : new NVector2(viewport.X - 10f, viewport.Y - (UseCompactCoordinates && hasCoordinates ? 30f : 12f));
        _mapFontQueue.QueueText(
            _gameTimeText,
            position,
            TravelMapPalette.SnowText,
            centered ? MapTextAlignment.BottomCenter : MapTextAlignment.BottomRight,
            CoordinateTextScale);
    }

    private float GetBottomOverlayHeight()
    {
        if (PlaceMapInformationBelowSurface
            || !ShowMapInformation
            || !ShowCoordinateBackdrop
            || (!_settings.ShowCoordinates && !_settings.ShowGameTime))
        {
            return 0f;
        }

        return UseCompactCoordinates && _settings.ShowCoordinates && _settings.ShowGameTime
            ? MiniMapVisualStyle.CoordinateStripHeight * 2f
            : MiniMapVisualStyle.CoordinateStripHeight;
    }

    private bool IsInside(NVector2 point, float margin) =>
        _shapeGeometry?.IntersectsDisc(point, margin) ?? false;

    private bool IsInsideViewport(NVector2 point, float margin) =>
        _shapeGeometry?.ContainsPoint(point, margin) ?? false;

    private void QueueShapeFrame(NVector2 viewport)
    {
        if (_frameQueue is null)
        {
            return;
        }

        if (EffectiveMapShape == MapShape.Square)
        {
            foreach (var primitive in MiniMapVisualStyle.CreateFramePrimitives(
                         viewport,
                         ShowFrameShadow,
                         FrameThickness,
                         FrameShadowColor,
                         FrameColor))
            {
                _frameQueue.QueueRectangle(primitive);
            }

            return;
        }

        if (ShowFrameShadow)
        {
            QueueBoundary(MapSurfacePrimitiveKind.FrameShadow, 0.5f, FrameShadowColor);
        }

        var frameCount = Math.Max(1, (int)MathF.Ceiling(FrameThickness));
        for (var index = 0; index < frameCount; index++)
        {
            QueueBoundary(MapSurfacePrimitiveKind.Frame, 1.5f + index, FrameColor);
        }
    }

    private void QueueBoundary(MapSurfacePrimitiveKind kind, float inset, Rgba32 color)
    {
        var geometry = MapShapeGeometry.Create(
            _drawMapTransform.ViewportSize,
            EffectiveMapShape,
            inset);
        var vertices = geometry.BoundaryVertices;
        for (var index = 0; index < vertices.Count; index++)
        {
            _frameQueue!.QueueLine(kind, vertices[index], vertices[(index + 1) % vertices.Count], color);
        }
    }

    private static Engine.Vector2 ToEngine(NVector2 value) => new(value.X, value.Y);

    protected virtual NVector2 GetSurfaceViewportSize() => new(ActualSize.X, ActualSize.Y);

    private MapShape EffectiveMapShape => ApplyConfiguredMiniMapShape
        ? _settings.MiniMapShape
        : MapShape.Square;

    private static Color ToEngineColor(Rgba32 color) => new(color.R, color.G, color.B, color.A);
}

public sealed class MiniMapRenderer : MapSurfaceWidget
{
    public const float InformationFooterHeight = 42f;
    public const float InformationFirstLineBaseline = 18f;
    public const float InformationSecondLineBaseline = 38f;

    private readonly TravelMapSettings _settings;
    private readonly Func<bool> _inputBlocked;
    private readonly Action _requestOpenLargeMap;
    private readonly Action<DeathMapMarker> _requestLocateLastDeath;
    private readonly MiniMapWheelInteraction _wheelInteraction;
    private readonly TravelMapUiController _uiController = new();
    private readonly MiniMapTouchTapState _touchTap = new();

    public MiniMapRenderer(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        TravelMapSettingsStore settingsStore,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<float> brightness,
        Func<bool> inputBlocked,
        Action<string> notify)
        : this(
            pixelSource,
            settings,
            settingsStore,
            playerPose,
            waypoints,
            creatures,
            brightness,
            inputBlocked,
            () => { },
            notify)
    {
    }

    public MiniMapRenderer(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        TravelMapSettingsStore settingsStore,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<float> brightness,
        Func<bool> inputBlocked,
        Action requestOpenLargeMap,
        Action<string> notify,
        Action<DeathMapMarker>? requestLocateLastDeath = null)
        : this(
            pixelSource,
            settings,
            settingsStore,
            playerPose,
            waypoints,
            creatures,
            () => null,
            brightness,
            inputBlocked,
            requestOpenLargeMap,
            notify,
            requestLocateLastDeath)
    {
    }

    public MiniMapRenderer(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        TravelMapSettingsStore settingsStore,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<DeathMapMarker?> lastDeath,
        Func<float> brightness,
        Func<bool> inputBlocked,
        Action requestOpenLargeMap,
        Action<string> notify,
        Action<DeathMapMarker>? requestLocateLastDeath = null)
        : base(pixelSource, settings, playerPose, waypoints, creatures, lastDeath, brightness)
    {
        _settings = settings;
        ArgumentNullException.ThrowIfNull(settingsStore);
        _inputBlocked = inputBlocked ?? throw new ArgumentNullException(nameof(inputBlocked));
        _requestOpenLargeMap = requestOpenLargeMap ?? throw new ArgumentNullException(nameof(requestOpenLargeMap));
        _requestLocateLastDeath = requestLocateLastDeath ?? (_ => _requestOpenLargeMap());
        ArgumentNullException.ThrowIfNull(notify);
        _wheelInteraction = new MiniMapWheelInteraction(
            settings,
            token => settingsStore.SaveAsync(settings, token),
            _ => notify(TravelMapText.Get(
                "miniMapZoomSaveFailedSession",
                "小地图比例未能保存，本次会话将保留当前值")));
        AutoCenterOnPlayer = true;
        ApplyConfiguredMiniMapOrientation = true;
        ApplyConfiguredMiniMapShape = true;
        ShowCompassOverlay = true;
        PlayerMarkerColor = TravelMapPalette.MiniMapPlayer;
        BackgroundColor = TravelMapPalette.MiniMapBackground;
        FrameColor = TravelMapPalette.MiniMapFrame;
        FrameShadowColor = TravelMapPalette.MiniMapFrameShadow;
        ShowSurveyCrosshair = false;
        ShowFrameShadow = true;
        ShowWaypointLabels = false;
        ShowCoordinateBackdrop = true;
        PlaceMapInformationBelowSurface = true;
        UseCompactCoordinates = true;
        DrawPlayerOutline = true;
        DeathMarkerSize = 13f;
        CoordinateTextScale = TravelMapTypography.MiniMapCoordinateScale;
        FrameThickness = MiniMapVisualStyle.FrameThickness;
        Transform = new MapTransform(NVector2.Zero, settings.MiniMapBlocksPerPixel, NVector2.One);
    }

    public override void MeasureOverride(Engine.Vector2 parentAvailableSize)
    {
        var size = _settings.MiniMapSize;
        DesiredSize = new Engine.Vector2(size, size + InformationFooterHeight);
        PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(size);
        Transform = Transform with { BlocksPerPixel = _settings.MiniMapBlocksPerPixel };
        IsDrawRequired = true;
    }

    protected override NVector2 GetSurfaceViewportSize() =>
        new(_settings.MiniMapSize, _settings.MiniMapSize);

    public override void Update()
    {
        if (!IsVisible || _inputBlocked())
        {
            _touchTap.Reset();
            return;
        }

        // Open the large map on a tap/click that lands on the mini map. Use the game's computed
        // Tap/Click gestures together with HitTestGlobal — the same path ClickableWidget/buttons
        // use — so a tap registers reliably on mobile, where raw TouchLocations are not delivered
        // here as a clean tap.
        if ((Input.Tap ?? Input.Click?.Start) is { } activationPoint
            && HitTestGlobal(activationPoint) == this)
        {
            var tapLocalEngine = ScreenToWidget(activationPoint);
            var tapLocal = new NVector2(tapLocalEngine.X, tapLocalEngine.Y);
            var tappedDeath = HitLastDeath(tapLocal);
            if (tappedDeath is not null)
            {
                _requestLocateLastDeath(tappedDeath);
            }
            else
            {
                _requestOpenLargeMap();
            }

            Input.Clear();
            return;
        }

        if (HandleTouchActivation())
        {
            return;
        }

        var pointer = Input.MousePosition;
        if (!pointer.HasValue)
        {
            return;
        }

        var localEngine = ScreenToWidget(pointer.Value);
        var local = new NVector2(localEngine.X, localEngine.Y);
        var hovered = ContainsLocalPoint(local);
        var activation = _uiController.HandleMiniMapActivation(
            Input.IsMouseButtonDownOnce(MouseButton.Left),
            hovered,
            inputBlocked: false);
        if (activation.Kind == TravelMapUiCommandKind.OpenLargeMap)
        {
            var death = HitLastDeath(local);
            if (death is not null)
            {
                _requestLocateLastDeath(death);
            }
            else
            {
                _requestOpenLargeMap();
            }
            Input.Clear();
            return;
        }

        var before = Transform;
        Transform = _wheelInteraction.HandleWheel(
            before,
            local,
            Input.MouseWheelMovement / 120f,
            hovered,
            inputBlocked: false);
        if (Transform != before)
        {
            Input.Clear();
        }
    }

    private bool HandleTouchActivation()
    {
        var consumed = false;
        for (var index = 0; index < Input.TouchLocations.Count; index++)
        {
            var touch = Input.TouchLocations[index];
            var localEngine = ScreenToWidget(touch.Position);
            var local = new NVector2(localEngine.X, localEngine.Y);
            var phase = touch.State switch
            {
                TouchLocationState.Pressed => MiniMapTouchPhase.Pressed,
                TouchLocationState.Moved => MiniMapTouchPhase.Moved,
                TouchLocationState.Released => MiniMapTouchPhase.Released,
                _ => MiniMapTouchPhase.Moved,
            };
            var update = _touchTap.Update(
                touch.Id,
                local,
                phase,
                ContainsLocalPoint(local),
                dragThreshold: 12f * MathF.Max(1f, GlobalScale));
            consumed |= update.Consumed;
            if (update.Activate)
            {
                var death = HitLastDeath(local);
                if (death is not null)
                {
                    _requestLocateLastDeath(death);
                }
                else
                {
                    _requestOpenLargeMap();
                }
                Input.Clear();
                return true;
            }
        }

        return consumed;
    }

    internal Task WhenSaveIdleAsync(CancellationToken cancellationToken = default) =>
        _wheelInteraction.WhenSaveIdleAsync(cancellationToken);

    public override void Dispose()
    {
        _wheelInteraction.Dispose();
        base.Dispose();
    }
}
