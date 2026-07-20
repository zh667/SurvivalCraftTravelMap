using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;

namespace SurvivalcraftTravelMap.UI;

internal sealed class MapShapeGeometry
{
    private const float Epsilon = 0.05f;
    private readonly Vector2[] _vertices;

    private MapShapeGeometry(
        MapShape shape,
        Vector2 viewportSize,
        Vector2 center,
        Vector2 halfSize,
        Vector2[] vertices)
    {
        Shape = shape;
        ViewportSize = viewportSize;
        Center = center;
        HalfSize = halfSize;
        _vertices = vertices;
    }

    public MapShape Shape { get; }

    public Vector2 ViewportSize { get; }

    public Vector2 Center { get; }

    public Vector2 HalfSize { get; }

    public IReadOnlyList<Vector2> BoundaryVertices => _vertices;

    public static MapShapeGeometry Create(Vector2 viewportSize, MapShape shape, float inset = 0f)
    {
        var safeWidth = MathF.Max(1f, float.IsFinite(viewportSize.X) ? viewportSize.X : 1f);
        var safeHeight = MathF.Max(1f, float.IsFinite(viewportSize.Y) ? viewportSize.Y : 1f);
        var safeViewport = new Vector2(safeWidth, safeHeight);
        var center = safeViewport / 2f;
        var safeInset = MathF.Max(0f, inset);
        var halfSize = shape is MapShape.Square or MapShape.RoundedSquare
            ? Vector2.Max((safeViewport / 2f) - new Vector2(safeInset), new Vector2(0.5f))
            : new Vector2(MathF.Max(0.5f, (MathF.Min(safeWidth, safeHeight) / 2f) - safeInset));
        return new MapShapeGeometry(
            shape,
            safeViewport,
            center,
            halfSize,
            CreateVertices(shape, center, halfSize));
    }

    public bool ContainsPoint(Vector2 point, float inset = 0f)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            return false;
        }

        var half = Vector2.Max(HalfSize - new Vector2(inset), new Vector2(Epsilon));
        var relative = Vector2.Abs(point - Center);
        return Shape switch
        {
            MapShape.Circle => relative.LengthSquared() <= (half.X * half.X) + Epsilon,
            MapShape.RoundedSquare => ContainsRoundedSquare(relative, half),
            _ => relative.X <= half.X + Epsilon && relative.Y <= half.Y + Epsilon,
        };
    }

    public bool IntersectsDisc(Vector2 center, float radius) =>
        ContainsPoint(center, -MathF.Max(0f, radius));

    public float RayDistance(Vector2 direction)
    {
        if (direction.LengthSquared() <= Epsilon)
        {
            return 0f;
        }

        direction = Vector2.Normalize(direction);
        var low = 0f;
        var high = HalfSize.Length() * 2f;
        for (var iteration = 0; iteration < 28; iteration++)
        {
            var middle = (low + high) / 2f;
            if (ContainsPoint(Center + (direction * middle)))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return MathF.Max(0f, low - Epsilon);
    }

    public float HorizontalSpanAt(float y)
    {
        if (!float.IsFinite(y)
            || !TryClipLine(
                new Vector2(Center.X - ViewportSize.X, y),
                new Vector2(Center.X + ViewportSize.X, y),
                out var start,
                out var end))
        {
            return 0f;
        }

        return MathF.Max(0f, end.X - start.X);
    }

    public IReadOnlyList<Vector2> ClipPolygon(IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Count < 3)
        {
            return [];
        }

        if (polygon.All(point => ContainsPoint(point)))
        {
            return polygon.ToArray();
        }

        if (Shape == MapShape.Circle && !CircleIntersectsPolygon(polygon))
        {
            return [];
        }

        foreach (var (start, end) in Edges())
        {
            if (polygon.All(point => !IsInsideEdge(point, start, end)))
            {
                return [];
            }
        }

        var input = polygon.ToList();
        foreach (var (start, end) in Edges())
        {
            if (input.Count == 0)
            {
                break;
            }

            var output = new List<Vector2>(input.Count + 1);
            var previous = input[^1];
            var previousInside = IsInsideEdge(previous, start, end);
            foreach (var current in input)
            {
                var currentInside = IsInsideEdge(current, start, end);
                if (currentInside != previousInside)
                {
                    output.Add(IntersectLines(previous, current, start, end));
                }

                if (currentInside)
                {
                    output.Add(current);
                }

                previous = current;
                previousInside = currentInside;
            }

            input = output;
        }

        return input;
    }

    public bool TryClipLine(Vector2 start, Vector2 end, out Vector2 clippedStart, out Vector2 clippedEnd)
    {
        var direction = end - start;
        var enter = 0f;
        var leave = 1f;
        foreach (var (edgeStart, edgeEnd) in Edges())
        {
            var edge = edgeEnd - edgeStart;
            var constant = Cross(edge, start - edgeStart);
            var coefficient = Cross(edge, direction);
            if (MathF.Abs(coefficient) <= Epsilon)
            {
                if (constant < -Epsilon)
                {
                    clippedStart = default;
                    clippedEnd = default;
                    return false;
                }

                continue;
            }

            var boundary = -constant / coefficient;
            if (coefficient > 0f)
            {
                enter = MathF.Max(enter, boundary);
            }
            else
            {
                leave = MathF.Min(leave, boundary);
            }

            if (enter > leave)
            {
                clippedStart = default;
                clippedEnd = default;
                return false;
            }
        }

        clippedStart = start + (direction * enter);
        clippedEnd = start + (direction * leave);
        return true;
    }

    private IEnumerable<(Vector2 Start, Vector2 End)> Edges()
    {
        for (var index = 0; index < _vertices.Length; index++)
        {
            yield return (_vertices[index], _vertices[(index + 1) % _vertices.Length]);
        }
    }

    private static Vector2[] CreateVertices(MapShape shape, Vector2 center, Vector2 half)
    {
        return shape switch
        {
            MapShape.Circle => CreateEllipseVertices(center, half, 40),
            MapShape.RoundedSquare => CreateRoundedSquareVertices(center, half),
            _ =>
            [
                center + new Vector2(-half.X, -half.Y),
                center + new Vector2(half.X, -half.Y),
                center + new Vector2(half.X, half.Y),
                center + new Vector2(-half.X, half.Y),
            ],
        };
    }

    private static Vector2[] CreateEllipseVertices(Vector2 center, Vector2 half, int segments)
    {
        var vertices = new Vector2[segments];
        for (var index = 0; index < segments; index++)
        {
            var angle = (-MathF.PI / 2f) + (MathF.Tau * index / segments);
            vertices[index] = center + new Vector2(MathF.Cos(angle) * half.X, MathF.Sin(angle) * half.Y);
        }

        return vertices;
    }

    private static Vector2[] CreateRoundedSquareVertices(Vector2 center, Vector2 half)
    {
        const int arcSegments = 6;
        var radius = MathF.Min(half.X, half.Y) * 0.18f;
        var corners = new[]
        {
            (new Vector2(center.X + half.X - radius, center.Y - half.Y + radius), -MathF.PI / 2f),
            (new Vector2(center.X + half.X - radius, center.Y + half.Y - radius), 0f),
            (new Vector2(center.X - half.X + radius, center.Y + half.Y - radius), MathF.PI / 2f),
            (new Vector2(center.X - half.X + radius, center.Y - half.Y + radius), MathF.PI),
        };
        var vertices = new List<Vector2>(arcSegments * corners.Length);
        foreach (var (corner, startAngle) in corners)
        {
            for (var index = 0; index <= arcSegments; index++)
            {
                var angle = startAngle + ((MathF.PI / 2f) * index / arcSegments);
                vertices.Add(corner + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius);
            }
        }

        return vertices.ToArray();
    }

    private static bool ContainsRoundedSquare(Vector2 absolute, Vector2 half)
    {
        if (absolute.X > half.X + Epsilon || absolute.Y > half.Y + Epsilon)
        {
            return false;
        }

        var radius = MathF.Min(half.X, half.Y) * 0.18f;
        var straight = half - new Vector2(radius);
        if (absolute.X <= straight.X || absolute.Y <= straight.Y)
        {
            return true;
        }

        return (absolute - straight).LengthSquared() <= (radius * radius) + Epsilon;
    }

    private bool CircleIntersectsPolygon(IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Any(point => ContainsPoint(point)) || PointInsideConvexPolygon(Center, polygon))
        {
            return true;
        }

        var radiusSquared = HalfSize.X * HalfSize.X;
        for (var index = 0; index < polygon.Count; index++)
        {
            if (DistanceSquaredToSegment(
                    Center,
                    polygon[index],
                    polygon[(index + 1) % polygon.Count]) <= radiusSquared + Epsilon)
            {
                return true;
            }
        }

        return false;
    }

    private static bool PointInsideConvexPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
    {
        float? sign = null;
        for (var index = 0; index < polygon.Count; index++)
        {
            var cross = Cross(
                polygon[(index + 1) % polygon.Count] - polygon[index],
                point - polygon[index]);
            if (MathF.Abs(cross) <= Epsilon)
            {
                continue;
            }

            var currentSign = MathF.Sign(cross);
            if (sign.HasValue && currentSign != sign.Value)
            {
                return false;
            }

            sign = currentSign;
        }

        return true;
    }

    private static float DistanceSquaredToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= Epsilon)
        {
            return Vector2.DistanceSquared(point, start);
        }

        var amount = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.DistanceSquared(point, start + (segment * amount));
    }

    private static bool IsInsideEdge(Vector2 point, Vector2 start, Vector2 end) =>
        Cross(end - start, point - start) >= -Epsilon;

    private static Vector2 IntersectLines(Vector2 start, Vector2 end, Vector2 edgeStart, Vector2 edgeEnd)
    {
        var direction = end - start;
        var edge = edgeEnd - edgeStart;
        var denominator = Cross(direction, edge);
        if (MathF.Abs(denominator) <= Epsilon)
        {
            return end;
        }

        var amount = Cross(edgeStart - start, edge) / denominator;
        return start + (direction * amount);
    }

    private static float Cross(Vector2 left, Vector2 right) =>
        (left.X * right.Y) - (left.Y * right.X);
}

internal sealed class ShapeClippedPrimitiveQueue(
    IMapSurfacePrimitiveQueue inner,
    MapShapeGeometry geometry) : IMapSurfacePrimitiveQueue
{
    public void QueueQuad(MapSurfacePrimitiveKind kind, Vector2 minimum, Vector2 maximum, Rgba32 color) =>
        QueueQuadCore(kind, minimum, maximum, color);

    public void QueueQuad(
        MapSurfacePrimitiveKind kind,
        Vector2 point1,
        Vector2 point2,
        Vector2 point3,
        Vector2 point4,
        Rgba32 color)
    {
        var polygon = new[] { point1, point2, point3, point4 };
        if (IsFullyInside(polygon))
        {
            inner.QueueQuad(kind, point1, point2, point3, point4, color);
            return;
        }

        QueuePolygon(kind, polygon, color);
    }

    public void QueueLine(MapSurfacePrimitiveKind kind, Vector2 start, Vector2 end, Rgba32 color)
    {
        if (geometry.TryClipLine(start, end, out var clippedStart, out var clippedEnd))
        {
            inner.QueueLine(kind, clippedStart, clippedEnd, color);
        }
    }

    public void QueueTriangle(MapSurfacePrimitiveKind kind, MapPlayerPrimitive primitive)
    {
        var polygon = new[] { primitive.Tip, primitive.Left, primitive.Right };
        if (IsFullyInside(polygon))
        {
            inner.QueueTriangle(kind, primitive);
            return;
        }

        QueuePolygon(kind, polygon, primitive.Color);
    }

    public void QueueTriangle(
        MapSurfacePrimitiveKind kind,
        Vector2 point1,
        Vector2 point2,
        Vector2 point3,
        Rgba32 color)
    {
        var polygon = new[] { point1, point2, point3 };
        if (IsFullyInside(polygon))
        {
            inner.QueueTriangle(kind, point1, point2, point3, color);
            return;
        }

        QueuePolygon(kind, polygon, color);
    }

    public void QueueDisc(
        MapSurfacePrimitiveKind kind,
        Vector2 center,
        float radius,
        Rgba32 color,
        int segments)
    {
        if (geometry.ContainsPoint(center, radius))
        {
            inner.QueueDisc(kind, center, radius, color, segments);
            return;
        }

        var count = Math.Max(8, segments);
        var polygon = new Vector2[count];
        for (var index = 0; index < count; index++)
        {
            var angle = MathF.Tau * index / count;
            polygon[index] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        QueuePolygon(kind, polygon, color);
    }

    public void QueueRectangle(MapFramePrimitive primitive)
    {
        var kind = primitive.Kind == MapFramePrimitiveKind.Shadow
            ? MapSurfacePrimitiveKind.FrameShadow
            : MapSurfacePrimitiveKind.Frame;
        QueueLine(kind, primitive.Minimum, new Vector2(primitive.Maximum.X, primitive.Minimum.Y), primitive.Color);
        QueueLine(kind, new Vector2(primitive.Maximum.X, primitive.Minimum.Y), primitive.Maximum, primitive.Color);
        QueueLine(kind, primitive.Maximum, new Vector2(primitive.Minimum.X, primitive.Maximum.Y), primitive.Color);
        QueueLine(kind, new Vector2(primitive.Minimum.X, primitive.Maximum.Y), primitive.Minimum, primitive.Color);
    }

    private void QueuePolygon(MapSurfacePrimitiveKind kind, IReadOnlyList<Vector2> polygon, Rgba32 color)
    {
        var clipped = geometry.ClipPolygon(polygon);
        if (clipped.Count < 3)
        {
            return;
        }

        for (var index = 1; index < clipped.Count - 1; index++)
        {
            inner.QueueTriangle(kind, clipped[0], clipped[index], clipped[index + 1], color);
        }
    }

    private void QueueQuadCore(
        MapSurfacePrimitiveKind kind,
        Vector2 minimum,
        Vector2 maximum,
        Rgba32 color)
    {
        var polygon = new[]
        {
            minimum,
            new Vector2(maximum.X, minimum.Y),
            maximum,
            new Vector2(minimum.X, maximum.Y),
        };
        if (IsFullyInside(polygon))
        {
            inner.QueueQuad(kind, minimum, maximum, color);
            return;
        }

        QueuePolygon(kind, polygon, color);
    }

    private bool IsFullyInside(IEnumerable<Vector2> polygon) =>
        polygon.All(point => geometry.ContainsPoint(point));
}
