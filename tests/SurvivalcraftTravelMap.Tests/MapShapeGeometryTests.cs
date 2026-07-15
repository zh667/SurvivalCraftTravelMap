using System.Numerics;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MapShapeGeometryTests
{
    [Theory]
    [InlineData(MapShape.Circle)]
    [InlineData(MapShape.Square)]
    [InlineData(MapShape.RoundedSquare)]
    public void Every_shape_contains_center_and_rejects_points_outside_viewport(MapShape shape)
    {
        var geometry = MapShapeGeometry.Create(new Vector2(192f), shape);

        Assert.True(geometry.ContainsPoint(new Vector2(96f)));
        Assert.False(geometry.ContainsPoint(new Vector2(-1f, 96f)));
        Assert.False(geometry.ContainsPoint(new Vector2(193f, 96f)));
    }

    [Theory]
    [InlineData(MapShape.Circle)]
    [InlineData(MapShape.RoundedSquare)]
    public void Non_square_shapes_reject_invisible_corner_input(MapShape shape)
    {
        var geometry = MapShapeGeometry.Create(new Vector2(192f), shape);

        Assert.False(geometry.ContainsPoint(new Vector2(2f)));
        Assert.True(geometry.ContainsPoint(new Vector2(96f, 2f)));
    }

    [Theory]
    [InlineData(MapShape.Circle)]
    [InlineData(MapShape.Square)]
    [InlineData(MapShape.RoundedSquare)]
    public void Polygon_clipping_never_returns_vertices_beyond_selected_shape(MapShape shape)
    {
        var geometry = MapShapeGeometry.Create(new Vector2(192f), shape);

        var clipped = geometry.ClipPolygon(
        [
            new Vector2(-20f),
            new Vector2(212f, -20f),
            new Vector2(212f),
            new Vector2(-20f, 212f),
        ]);

        Assert.True(clipped.Count >= 3);
        Assert.All(clipped, point => Assert.True(geometry.ContainsPoint(point)));
    }

    [Theory]
    [InlineData(MapShape.Circle)]
    [InlineData(MapShape.RoundedSquare)]
    public void Line_clipping_ends_at_the_same_visible_boundary_used_for_hit_testing(MapShape shape)
    {
        var geometry = MapShapeGeometry.Create(new Vector2(192f), shape);

        Assert.True(geometry.TryClipLine(
            new Vector2(-20f, 96f),
            new Vector2(212f, 96f),
            out var start,
            out var end));

        Assert.True(geometry.ContainsPoint(start));
        Assert.True(geometry.ContainsPoint(end));
        Assert.InRange(start.X, -0.001f, 96f);
        Assert.InRange(end.X, 96f, 192.001f);
    }

    [Fact]
    public void Circle_on_a_wide_large_map_remains_a_circle_instead_of_an_ellipse()
    {
        var geometry = MapShapeGeometry.Create(new Vector2(800f, 480f), MapShape.Circle);

        Assert.Equal(240f, geometry.HalfSize.X);
        Assert.Equal(geometry.HalfSize.X, geometry.HalfSize.Y);
        Assert.False(geometry.ContainsPoint(new Vector2(10f, 240f)));
    }
}
