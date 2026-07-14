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
        Rgba32 color) => queue.QueueText(
            text,
            position,
            color,
            MapTextAlignment.Default,
            TravelMapTypography.SecondaryLabelScale);

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
        float scale) => queue.QueueText(
            text,
            position,
            color,
            MapTextAlignment.BottomLeft,
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

    public static MapCoordinateBackdropPrimitive CreateCoordinateBackdrop(NVector2 size) => new(
        new NVector2(0f, MathF.Max(0f, size.Y - CoordinateStripHeight)),
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

public class MapSurfaceWidget : Widget, ITravelMapRenderSink
{
    private static readonly Color SurveyCyan = ToEngineColor(TravelMapPalette.SurveyCyan);

    private readonly IExploredMapPixelSource _pixelSource;
    private readonly TravelMapSettings _settings;
    private readonly Func<PlayerMapPose> _playerPose;
    private readonly Func<IReadOnlyList<Waypoint>> _waypoints;
    private readonly Func<float> _brightness;
    private readonly BitmapFont _font;
    private FlatBatch2D? _flatBatch;
    private FontBatch2D? _fontBatch;
    private IMapFontQueue? _mapFontQueue;
    private Engine.Matrix _drawTransform;
    private MapTransform _drawMapTransform;
    private NVector2? _labelPointer;
    private IReadOnlyList<Waypoint> _drawWaypoints = Array.Empty<Waypoint>();
    private (int X, int Y, int Z) _lastCoordinate;
    private string _coordinateText = string.Empty;

    public MapSurfaceWidget(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<float> brightness)
    {
        _pixelSource = pixelSource ?? throw new ArgumentNullException(nameof(pixelSource));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _playerPose = playerPose ?? throw new ArgumentNullException(nameof(playerPose));
        _waypoints = waypoints ?? throw new ArgumentNullException(nameof(waypoints));
        _brightness = brightness ?? throw new ArgumentNullException(nameof(brightness));
        _font = ContentManager.Get<BitmapFont>("Fonts/Pericles");
        IsDrawRequired = true;
        ClampToBounds = true;
    }

    public bool AutoCenterOnPlayer { get; set; }

    public bool ShowWaypointLabels { get; set; }

    public bool ShowSurveyCrosshair { get; set; } = true;

    public bool ShowFrameShadow { get; set; }

    public bool ShowCoordinateBackdrop { get; set; }

    public bool UseCompactCoordinates { get; set; }

    public bool DrawPlayerOutline { get; set; }

    public float CoordinateTextScale { get; set; } = TravelMapTypography.SecondaryLabelScale;

    public float FrameThickness { get; set; } = 1f;

    public Rgba32 PlayerMarkerColor { get; set; } = TravelMapPalette.SurveyCyan;

    public Rgba32 BackgroundColor { get; set; } = TravelMapPalette.Basalt;

    public Rgba32 FrameColor { get; set; } = TravelMapPalette.Moss;

    public Rgba32 FrameShadowColor { get; set; } = new(0x12, 0x12, 0x12, 0x80);

    public float PlayerArrowSize { get; set; } = 32f;

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

    public override void MeasureOverride(Engine.Vector2 parentAvailableSize)
    {
        DesiredSize = new Engine.Vector2(float.PositiveInfinity);
        IsDrawRequired = true;
    }

    public override void Draw(DrawContext dc)
    {
        var pose = _playerPose();
        var viewport = new NVector2(ActualSize.X, ActualSize.Y);
        if (viewport.X <= 0f || viewport.Y <= 0f)
        {
            return;
        }

        var center = AutoCenterOnPlayer
            ? new NVector2(pose.Position.X, pose.Position.Z)
            : Transform.Center;
        Transform = Transform with { Center = center, ViewportSize = viewport };
        _drawMapTransform = Transform;
        _drawTransform = GlobalTransform;
        _flatBatch = dc.PrimitivesRenderer2D.FlatBatch(
            0,
            depthStencilState: DepthStencilState.None,
            blendState: BlendState.AlphaBlend);
        _fontBatch = dc.PrimitivesRenderer2D.FontBatch(
            _font,
            1,
            depthStencilState: DepthStencilState.None,
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.PointClamp);
        _mapFontQueue = new EngineMapFontQueue(_fontBatch);
        var triangleStart = _flatBatch.TriangleVertices.Count;
        var lineStart = _flatBatch.LineVertices.Count;
        var textStart = _fontBatch.TriangleVertices.Count;

        _flatBatch.QueueQuad(
            Engine.Vector2.Zero,
            new Engine.Vector2(ActualSize.X, ActualSize.Y),
            0f,
            new Color(ToEngineColor(BackgroundColor), 224));

        var terrainBrightness = _settings.UseDayNightTint ? _brightness() : 1f;
        TravelMapRenderModel.RenderTerrain(_pixelSource, Transform, terrainBrightness, this);
        _drawWaypoints = _waypoints();
        TravelMapRenderModel.RenderOverlays(
            new MapOverlayState(
                pose.Position,
                pose.Heading,
                PlayerArrowSize,
                _drawWaypoints,
                _settings.ShowCoordinates,
                PlayerMarkerColor),
            this);
        if (ShowSurveyCrosshair)
        {
            QueueSurveyCrosshair(Transform.WorldToScreen(new NVector2(pose.Position.X, pose.Position.Z)));
        }

        foreach (var primitive in MiniMapVisualStyle.CreateFramePrimitives(
                     viewport,
                     ShowFrameShadow,
                     FrameThickness,
                     FrameShadowColor,
                     FrameColor))
        {
            _flatBatch.QueueRectangle(
                ToEngine(primitive.Minimum),
                ToEngine(primitive.Maximum),
                0f,
                ToEngineColor(primitive.Color));
        }

        _flatBatch.TransformTriangles(_drawTransform, triangleStart);
        _flatBatch.TransformLines(_drawTransform, lineStart);
        _fontBatch.TransformTriangles(_drawTransform, textStart);
        _flatBatch = null;
        _fontBatch = null;
        _mapFontQueue = null;
        _drawWaypoints = Array.Empty<Waypoint>();
    }

    public void TerrainCell(MapTerrainCell cell)
    {
        _flatBatch!.QueueQuad(ToEngine(cell.ScreenMinimum), ToEngine(cell.ScreenMaximum), 0f, ToEngineColor(cell.Color));
    }

    public void ExplorationBoundary(MapBoundaryEdge edge)
    {
        _flatBatch!.QueueLine(ToEngine(edge.Start), ToEngine(edge.End), 0f, ToEngineColor(edge.Color));
    }

    public void Player(NVector3 position, float heading, float size, Rgba32 color)
    {
        var center = _drawMapTransform.WorldToScreen(new NVector2(position.X, position.Z));
        foreach (var primitive in MiniMapVisualStyle.CreatePlayerPrimitives(
                     center,
                     heading,
                     size,
                     color,
                     DrawPlayerOutline))
        {
            _flatBatch!.QueueTriangle(
                ToEngine(primitive.Tip),
                ToEngine(primitive.Left),
                ToEngine(primitive.Right),
                0f,
                ToEngineColor(primitive.Color));
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
        _flatBatch!.QueueQuad(
            ToEngine(center + new NVector2(0f, -radius)),
            ToEngine(center + new NVector2(radius, 0f)),
            ToEngine(center + new NVector2(0f, radius)),
            ToEngine(center + new NVector2(-radius, 0f)),
            0f,
            ToEngineColor(color));
        if (ShowWaypointLabels && ShouldDrawWaypointLabel(waypoint, center))
        {
            MiniMapTextRenderer.QueueWaypointLabel(
                _mapFontQueue!,
                waypoint.Name,
                center + new NVector2(9f, -9f),
                TravelMapPalette.SnowText);
        }
    }

    public void Label(string text, NVector3 worldPosition, Rgba32 color)
    {
        if (!_settings.ShowCoordinates)
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

        if (ShowCoordinateBackdrop)
        {
            var backdrop = MiniMapVisualStyle.CreateCoordinateBackdrop(
                new NVector2(ActualSize.X, ActualSize.Y));
            _flatBatch!.QueueQuad(
                ToEngine(backdrop.Minimum),
                ToEngine(backdrop.Maximum),
                0f,
                ToEngineColor(backdrop.Color));
        }

        MiniMapTextRenderer.QueueCoordinates(
            _mapFontQueue!,
            _coordinateText,
            new NVector2(10f, ActualSize.Y - 12f),
            color,
            CoordinateTextScale);
    }

    private bool ShouldDrawWaypointLabel(Waypoint waypoint, NVector2 position)
    {
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
                alignment == MapTextAlignment.BottomLeft
                    ? TextAnchor.Bottom | TextAnchor.Left
                    : TextAnchor.Default,
                new Engine.Vector2(scale),
                Engine.Vector2.Zero);
    }

    private void QueueSurveyCrosshair(NVector2 center)
    {
        if (!IsInside(center, 24f))
        {
            return;
        }

        var c = ToEngine(center);
        _flatBatch!.QueueLine(c + new Engine.Vector2(-22f, 0f), c + new Engine.Vector2(-9f, 0f), 0f, SurveyCyan);
        _flatBatch.QueueLine(c + new Engine.Vector2(9f, 0f), c + new Engine.Vector2(22f, 0f), 0f, SurveyCyan);
        _flatBatch.QueueLine(c + new Engine.Vector2(0f, -22f), c + new Engine.Vector2(0f, -9f), 0f, SurveyCyan);
        _flatBatch.QueueLine(c + new Engine.Vector2(0f, 9f), c + new Engine.Vector2(0f, 22f), 0f, SurveyCyan);
    }

    private bool IsInside(NVector2 point, float margin) =>
        point.X >= -margin
        && point.Y >= -margin
        && point.X <= ActualSize.X + margin
        && point.Y <= ActualSize.Y + margin;

    private static Engine.Vector2 ToEngine(NVector2 value) => new(value.X, value.Y);

    private static Color ToEngineColor(Rgba32 color) => new(color.R, color.G, color.B, color.A);
}

public sealed class MiniMapRenderer : MapSurfaceWidget
{
    private readonly TravelMapSettings _settings;
    private readonly Func<bool> _inputBlocked;
    private readonly Action _requestOpenLargeMap;
    private readonly MiniMapWheelInteraction _wheelInteraction;
    private readonly TravelMapUiController _uiController = new();

    public MiniMapRenderer(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        TravelMapSettingsStore settingsStore,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<float> brightness,
        Func<bool> inputBlocked,
        Action<string> notify)
        : this(
            pixelSource,
            settings,
            settingsStore,
            playerPose,
            waypoints,
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
        Func<float> brightness,
        Func<bool> inputBlocked,
        Action requestOpenLargeMap,
        Action<string> notify)
        : base(pixelSource, settings, playerPose, waypoints, brightness)
    {
        _settings = settings;
        ArgumentNullException.ThrowIfNull(settingsStore);
        _inputBlocked = inputBlocked ?? throw new ArgumentNullException(nameof(inputBlocked));
        _requestOpenLargeMap = requestOpenLargeMap ?? throw new ArgumentNullException(nameof(requestOpenLargeMap));
        ArgumentNullException.ThrowIfNull(notify);
        _wheelInteraction = new MiniMapWheelInteraction(
            settings,
            token => settingsStore.SaveAsync(settings, token),
            _ => notify("小地图比例未能保存，本次会话将保留当前值"));
        AutoCenterOnPlayer = true;
        PlayerMarkerColor = TravelMapPalette.MiniMapPlayer;
        BackgroundColor = TravelMapPalette.MiniMapBackground;
        FrameColor = TravelMapPalette.MiniMapFrame;
        FrameShadowColor = TravelMapPalette.MiniMapFrameShadow;
        ShowSurveyCrosshair = false;
        ShowFrameShadow = true;
        ShowWaypointLabels = false;
        ShowCoordinateBackdrop = true;
        UseCompactCoordinates = true;
        DrawPlayerOutline = true;
        CoordinateTextScale = TravelMapTypography.MiniMapCoordinateScale;
        FrameThickness = MiniMapVisualStyle.FrameThickness;
        Transform = new MapTransform(NVector2.Zero, settings.MiniMapBlocksPerPixel, NVector2.One);
    }

    public override void MeasureOverride(Engine.Vector2 parentAvailableSize)
    {
        var size = _settings.MiniMapSize;
        DesiredSize = new Engine.Vector2(size, size);
        PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(size);
        Transform = Transform with { BlocksPerPixel = _settings.MiniMapBlocksPerPixel };
        IsDrawRequired = true;
    }

    public override void Update()
    {
        var pointer = Input.MousePosition;
        if (!IsVisible || !pointer.HasValue)
        {
            return;
        }

        var localEngine = ScreenToWidget(pointer.Value);
        var local = new NVector2(localEngine.X, localEngine.Y);
        var hovered = local.X >= 0f
            && local.Y >= 0f
            && local.X <= ActualSize.X
            && local.Y <= ActualSize.Y;
        var inputBlocked = _inputBlocked();
        var activation = _uiController.HandleMiniMapActivation(
            Input.IsMouseButtonDownOnce(MouseButton.Left),
            hovered,
            inputBlocked);
        if (activation.Kind == TravelMapUiCommandKind.OpenLargeMap)
        {
            _requestOpenLargeMap();
            Input.Clear();
            return;
        }

        var before = Transform;
        Transform = _wheelInteraction.HandleWheel(
            before,
            local,
            Input.MouseWheelMovement / 120f,
            hovered,
            inputBlocked);
        if (Transform != before)
        {
            Input.Clear();
        }
    }

    internal Task WhenSaveIdleAsync(CancellationToken cancellationToken = default) =>
        _wheelInteraction.WhenSaveIdleAsync(cancellationToken);

    public override void Dispose()
    {
        _wheelInteraction.Dispose();
        base.Dispose();
    }
}
