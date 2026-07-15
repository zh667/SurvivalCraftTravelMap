using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

internal enum CompassDirection
{
    North,
    East,
    South,
    West,
}

internal enum CompassBoundaryShape
{
    Circle,
    Square,
    Hexagon,
    RoundedSquare,
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
        CompassBoundaryShape shape,
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
        var center = viewportSize / 2f;
        var halfExtents = Vector2.Max(center - new Vector2(inset), new Vector2(1f));
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
            var distance = BoundaryDistance(direction, halfExtents, shape);
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

    private static float BoundaryDistance(
        Vector2 direction,
        Vector2 halfExtents,
        CompassBoundaryShape shape)
    {
        var xDistance = MathF.Abs(direction.X) > 0.00001f
            ? halfExtents.X / MathF.Abs(direction.X)
            : float.PositiveInfinity;
        var yDistance = MathF.Abs(direction.Y) > 0.00001f
            ? halfExtents.Y / MathF.Abs(direction.Y)
            : float.PositiveInfinity;
        var squareDistance = MathF.Min(xDistance, yDistance);
        if (shape == CompassBoundaryShape.Square)
        {
            return squareDistance;
        }

        var low = 0f;
        var high = squareDistance;
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var middle = (low + high) / 2f;
            if (Contains(direction * middle, halfExtents, shape))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static bool Contains(
        Vector2 relative,
        Vector2 halfExtents,
        CompassBoundaryShape shape)
    {
        var absolute = Vector2.Abs(relative);
        return shape switch
        {
            CompassBoundaryShape.Circle =>
                ((absolute.X * absolute.X) / (halfExtents.X * halfExtents.X))
                + ((absolute.Y * absolute.Y) / (halfExtents.Y * halfExtents.Y)) <= 1f,
            CompassBoundaryShape.Hexagon =>
                absolute.Y <= halfExtents.Y
                && absolute.X <= halfExtents.X
                    - ((absolute.Y / halfExtents.Y) * halfExtents.X * 0.5f),
            CompassBoundaryShape.RoundedSquare => ContainsRoundedSquare(absolute, halfExtents),
            _ => absolute.X <= halfExtents.X && absolute.Y <= halfExtents.Y,
        };
    }

    private static bool ContainsRoundedSquare(Vector2 absolute, Vector2 halfExtents)
    {
        if (absolute.X > halfExtents.X || absolute.Y > halfExtents.Y)
        {
            return false;
        }

        var radius = MathF.Min(halfExtents.X, halfExtents.Y) * 0.18f;
        var straightX = halfExtents.X - radius;
        var straightY = halfExtents.Y - radius;
        if (absolute.X <= straightX || absolute.Y <= straightY)
        {
            return true;
        }

        var corner = new Vector2(absolute.X - straightX, absolute.Y - straightY);
        return corner.LengthSquared() <= radius * radius;
    }
}
