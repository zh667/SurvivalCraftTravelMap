using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;

namespace SurvivalcraftTravelMap.UI;

internal readonly record struct DeathMarkerProjection(
    Vector2 Position,
    Vector2 Direction,
    bool IsOffscreen);

internal static class DeathMarkerTracking
{
    public static DeathMarkerProjection Project(
        MapTransform transform,
        Vector2 viewportSize,
        MapShape shape,
        Vector2 worldPosition,
        float inset)
    {
        var geometry = MapShapeGeometry.Create(viewportSize, shape);
        var screen = transform.WorldToScreen(worldPosition);
        var safeInset = MathF.Max(0f, float.IsFinite(inset) ? inset : 0f);
        var delta = screen - geometry.Center;
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : new Vector2(0f, -1f);
        if (geometry.ContainsPoint(screen, safeInset))
        {
            return new DeathMarkerProjection(screen, direction, IsOffscreen: false);
        }

        var distance = MathF.Max(0f, geometry.RayDistance(direction) - safeInset);
        return new DeathMarkerProjection(
            geometry.Center + (direction * distance),
            direction,
            IsOffscreen: true);
    }
}
