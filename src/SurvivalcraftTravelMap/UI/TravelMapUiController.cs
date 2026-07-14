using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Waypoints;

namespace SurvivalcraftTravelMap.UI;

public static class TravelMapTypography
{
    public const float SecondaryLabelScale = 0.8f;

    public const float MiniMapCoordinateScale = 0.65f;
}

public readonly record struct TravelMapFocusState(
    bool HasTextFocus,
    bool HasChatFocus,
    bool HasModalFocus)
{
    public static TravelMapFocusState Clear { get; } = new(false, false, false);

    public bool AllowsMapHotkey => !HasTextFocus && !HasChatFocus && !HasModalFocus;
}

public readonly record struct TravelMapInputFocusSignals(
    bool HasFocusedTextBox,
    bool IsChatVisible,
    bool HasFocusedChatTextBox,
    bool HasModalCapture);

public static class TravelMapInputFocusEvaluator
{
    public static TravelMapFocusState Evaluate(TravelMapInputFocusSignals signals) => new(
        signals.HasFocusedTextBox,
        signals.IsChatVisible && signals.HasFocusedChatTextBox,
        signals.HasModalCapture);
}

public enum TravelMapUiCommandKind
{
    None,
    OpenLargeMap,
    CloseLargeMap,
    Pan,
    Zoom,
    ShowGroundMenu,
    ShowWaypointMenu,
    ShowUnexploredMessage,
}

public enum TravelMapContextAction
{
    TeleportNearby,
    AddWaypoint,
    TeleportToWaypoint,
    RenameWaypoint,
    DeleteWaypoint,
    Cancel,
}

public sealed record TravelMapContextMenu(
    Vector2 WorldPosition,
    Guid? WaypointId,
    IReadOnlyList<TravelMapContextAction> Actions);

public sealed record TravelMapUiCommand(
    TravelMapUiCommandKind Kind,
    MapTransform? Transform = null,
    TravelMapContextMenu? ContextMenu = null)
{
    public static TravelMapUiCommand None { get; } = new(TravelMapUiCommandKind.None);
}

public sealed class TravelMapUiController
{
    private static readonly TravelMapContextAction[] GroundActions =
    [
        TravelMapContextAction.TeleportNearby,
        TravelMapContextAction.AddWaypoint,
        TravelMapContextAction.Cancel,
    ];

    private static readonly TravelMapContextAction[] WaypointActions =
    [
        TravelMapContextAction.TeleportToWaypoint,
        TravelMapContextAction.RenameWaypoint,
        TravelMapContextAction.DeleteWaypoint,
        TravelMapContextAction.Cancel,
    ];

    public TravelMapUiCommand HandleOpenHotkey(bool isPressed, TravelMapFocusState focus) =>
        isPressed && focus.AllowsMapHotkey
            ? new TravelMapUiCommand(TravelMapUiCommandKind.OpenLargeMap)
            : TravelMapUiCommand.None;

    public TravelMapUiCommand HandleMiniMapActivation(
        bool isPressed,
        bool isHovered,
        bool inputBlocked) =>
        isPressed && isHovered && !inputBlocked
            ? new TravelMapUiCommand(TravelMapUiCommandKind.OpenLargeMap)
            : TravelMapUiCommand.None;

    public TravelMapUiCommand HandleToggleHotkey(
        bool isPressed,
        bool isOpen,
        TravelMapFocusState focus) =>
        isPressed && focus.AllowsMapHotkey
            ? new TravelMapUiCommand(
                isOpen ? TravelMapUiCommandKind.CloseLargeMap : TravelMapUiCommandKind.OpenLargeMap)
            : TravelMapUiCommand.None;

    public MapTransform CenterLargeMap(
        Vector2 playerPosition,
        Vector2 viewportSize,
        float blocksPerPixel)
    {
        ValidateZoomRange(blocksPerPixel, blocksPerPixel);
        return new MapTransform(playerPosition, blocksPerPixel, viewportSize);
    }

    public TravelMapUiCommand HandleWheel(
        MapTransform transform,
        Vector2 pointer,
        float wheelSteps,
        bool isHovered,
        float minimumBlocksPerPixel,
        float maximumBlocksPerPixel)
    {
        ValidateZoomRange(minimumBlocksPerPixel, maximumBlocksPerPixel);
        if (!isHovered || wheelSteps == 0f || !float.IsFinite(wheelSteps))
        {
            return TravelMapUiCommand.None;
        }

        var requested = transform.BlocksPerPixel * MathF.Pow(MathF.Sqrt(2f), -wheelSteps);
        var clamped = Math.Clamp(requested, minimumBlocksPerPixel, maximumBlocksPerPixel);
        var zoomed = transform.ZoomAt(pointer, clamped / transform.BlocksPerPixel);
        return new TravelMapUiCommand(TravelMapUiCommandKind.Zoom, zoomed);
    }

    public TravelMapUiCommand HandlePan(
        MapTransform transform,
        Vector2 screenDelta,
        bool isDragging)
    {
        if (!isDragging || screenDelta == Vector2.Zero)
        {
            return TravelMapUiCommand.None;
        }

        var panned = transform with
        {
            Center = transform.Center - (screenDelta * transform.BlocksPerPixel),
        };
        return new TravelMapUiCommand(TravelMapUiCommandKind.Pan, panned);
    }

    public TravelMapUiCommand HandleRightClick(
        Vector2 worldPosition,
        bool isExplored,
        Waypoint? waypointHit)
    {
        if (waypointHit is not null)
        {
            return new TravelMapUiCommand(
                TravelMapUiCommandKind.ShowWaypointMenu,
                ContextMenu: new TravelMapContextMenu(
                    worldPosition,
                    waypointHit.Id,
                    WaypointActions));
        }

        if (isExplored)
        {
            return new TravelMapUiCommand(
                TravelMapUiCommandKind.ShowGroundMenu,
                ContextMenu: new TravelMapContextMenu(worldPosition, null, GroundActions));
        }

        return new TravelMapUiCommand(TravelMapUiCommandKind.ShowUnexploredMessage);
    }

    private static void ValidateZoomRange(float minimum, float maximum)
    {
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || minimum <= 0f || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "The zoom range must be finite, positive, and ordered.");
        }
    }
}
