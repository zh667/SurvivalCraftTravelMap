using System.Numerics;
using SurvivalcraftTravelMap.Settings;

namespace SurvivalcraftTravelMap.UI;

internal enum CompassDirection
{
    North,
    East,
    South,
    West,
}

internal readonly record struct CompassLabel(
    CompassDirection Direction,
    Vector2 Position,
    bool IsNorth);

internal static class CompassLayout
{
    private const float BaseInset = 12f;

    private static readonly (CompassDirection Direction, Vector2 WorldDirection)[] Directions =
    [
        (CompassDirection.North, new Vector2(0f, -1f)),
        (CompassDirection.East, new Vector2(1f, 0f)),
        (CompassDirection.South, new Vector2(0f, 1f)),
        (CompassDirection.West, new Vector2(-1f, 0f)),
    ];

    public static IReadOnlyList<CompassLabel> Create(
        Vector2 viewportSize,
        float rotationRadians,
        MapShape shape,
        bool showNorth,
        bool showOtherDirections,
        float fontScale,
        float bottomReservedHeight)
    {
        if (!float.IsFinite(viewportSize.X)
            || !float.IsFinite(viewportSize.Y)
            || viewportSize.X <= 0f
            || viewportSize.Y <= 0f)
        {
            return [];
        }

        var safeRotation = float.IsFinite(rotationRadians) ? rotationRadians : 0f;
        var safeScale = Math.Clamp(float.IsFinite(fontScale) ? fontScale : 1f, 0.5f, 2f);
        var inset = BaseInset + (MathF.Max(0f, safeScale - 1f) * 4f);
        var geometry = MapShapeGeometry.Create(viewportSize, shape, inset);
        var center = geometry.Center;
        var reserve = Math.Clamp(
            float.IsFinite(bottomReservedHeight) ? bottomReservedHeight : 0f,
            0f,
            MathF.Max(0f, viewportSize.Y - (inset * 2f)));
        var labels = new List<CompassLabel>(4);
        var sine = MathF.Sin(safeRotation);
        var cosine = MathF.Cos(safeRotation);

        foreach (var item in Directions)
        {
            var isNorth = item.Direction == CompassDirection.North;
            if ((isNorth && !showNorth) || (!isNorth && !showOtherDirections))
            {
                continue;
            }

            var direction = new Vector2(
                (item.WorldDirection.X * cosine) - (item.WorldDirection.Y * sine),
                (item.WorldDirection.X * sine) + (item.WorldDirection.Y * cosine));
            var distance = geometry.RayDistance(direction);
            if (direction.Y > 0.00001f)
            {
                var maximumY = viewportSize.Y - inset - reserve;
                distance = MathF.Min(distance, MathF.Max(0f, (maximumY - center.Y) / direction.Y));
            }

            var position = center + (direction * distance);
            position = Vector2.Clamp(position, Vector2.Zero, viewportSize);
            labels.Add(new CompassLabel(
                item.Direction,
                position,
                isNorth));
        }

        return labels;
    }

}
