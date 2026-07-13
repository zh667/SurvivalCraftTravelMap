using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal static class TravelMapOverlayLayout
{
    internal static Vector2 PlaceTopRight(
        Vector2 guiLogicalSize,
        Vector2 overlaySize,
        float rightMargin,
        float topMargin)
    {
        var availableWidth = MathF.Max(0f, guiLogicalSize.X);
        var availableHeight = MathF.Max(0f, guiLogicalSize.Y);
        var width = MathF.Max(0f, overlaySize.X);
        var height = MathF.Max(0f, overlaySize.Y);
        var maximumX = MathF.Max(0f, availableWidth - width);
        var maximumY = MathF.Max(0f, availableHeight - height);
        var x = Math.Clamp(
            availableWidth - width - MathF.Max(0f, rightMargin),
            0f,
            maximumX);
        var y = Math.Clamp(MathF.Max(0f, topMargin), 0f, maximumY);
        return new Vector2(x, y);
    }
}
