using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal readonly record struct TravelMapHudPositions(
    Vector2 MiniMap,
    Vector2 TeleportButton);

internal static class TravelMapOverlayLayout
{
    private const float RightMargin = 76f;
    private const float TopMargin = 24f;
    private const float TeleportGap = 4f;
    private static readonly Vector2 TeleportButtonSize = new(48f, 46f);

    internal static Vector2 PlaceTopRight(
        Vector2 guiLogicalSize,
        Vector2 overlaySize,
        float rightMargin,
        float topMargin)
    {
        var availableWidth = NormalizeExtent(guiLogicalSize.X);
        var availableHeight = NormalizeExtent(guiLogicalSize.Y);
        var width = NormalizeExtent(overlaySize.X);
        var height = NormalizeExtent(overlaySize.Y);
        var maximumX = MathF.Max(0f, availableWidth - width);
        var maximumY = MathF.Max(0f, availableHeight - height);
        var x = Math.Clamp(
            availableWidth - width - NormalizeExtent(rightMargin),
            0f,
            maximumX);
        var y = Math.Clamp(NormalizeExtent(topMargin), 0f, maximumY);
        return new Vector2(x, y);
    }

    internal static TravelMapHudPositions PlaceHud(
        Vector2 guiLogicalSize,
        float miniMapSize)
    {
        var size = NormalizeExtent(miniMapSize);
        var miniMap = PlaceTopRight(
            guiLogicalSize,
            new Vector2(size),
            RightMargin,
            TopMargin);
        var teleportButton = ClampToGui(
            new Vector2(
                miniMap.X + size - TeleportButtonSize.X,
                miniMap.Y + size + TeleportGap),
            TeleportButtonSize,
            guiLogicalSize);

        return new TravelMapHudPositions(miniMap, teleportButton);
    }

    private static Vector2 ClampToGui(
        Vector2 position,
        Vector2 widgetSize,
        Vector2 guiLogicalSize)
    {
        var availableWidth = NormalizeExtent(guiLogicalSize.X);
        var availableHeight = NormalizeExtent(guiLogicalSize.Y);
        var maximumX = MathF.Max(0f, availableWidth - NormalizeExtent(widgetSize.X));
        var maximumY = MathF.Max(0f, availableHeight - NormalizeExtent(widgetSize.Y));

        return new Vector2(
            Math.Clamp(NormalizeExtent(position.X), 0f, maximumX),
            Math.Clamp(NormalizeExtent(position.Y), 0f, maximumY));
    }

    private static float NormalizeExtent(float value) =>
        float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
}
