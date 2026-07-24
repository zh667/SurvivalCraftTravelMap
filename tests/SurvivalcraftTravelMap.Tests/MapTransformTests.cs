using System.Numerics;
using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MapTransformTests
{
    [Fact]
    public void WorldToScreen_interprets_scale_as_blocks_per_pixel()
    {
        var map = new MapTransform(new Vector2(100, -50), 2.0f, new Vector2(800, 600));

        var result = map.WorldToScreen(new Vector2(104, -44));

        // The plane is rotated 180 degrees (Center - world), so a world point +4/-6 blocks from
        // the center lands -2/-3 pixels from the viewport center instead of +2/+3.
        AssertVectorNear(new Vector2(398, 297), result, 0.001f);
    }

    [Fact]
    public void ScreenToWorld_round_trips_world_coordinates()
    {
        var map = new MapTransform(new Vector2(100, -50), 2.0f, new Vector2(800, 600));
        var world = new Vector2(-12.5f, 78.25f);

        var result = map.ScreenToWorld(map.WorldToScreen(world));

        AssertVectorNear(world, result, 0.001f);
    }

    [Fact]
    public void ZoomAt_keeps_world_coordinate_under_cursor()
    {
        var map = new MapTransform(new Vector2(100, -50), 2.0f, new Vector2(800, 600));
        var cursor = new Vector2(610, 210);
        var before = map.ScreenToWorld(cursor);

        var after = map.ZoomAt(cursor, 0.5f);

        AssertVectorNear(before, after.ScreenToWorld(cursor), 0.001f);
    }

    [Fact]
    public void Rotated_transform_places_the_heading_direction_at_the_top()
    {
        // With the 180-degree plane rotation (Center - world), the heading-up rotation that puts
        // the +X world direction at the top is +PI/2 (it is -heading, and facing +X now yields
        // heading -PI/2). The forward point 10 blocks along +X still lands at the top edge.
        var map = new MapTransform(
            Vector2.Zero,
            1f,
            new Vector2(200f),
            RotationRadians: MathF.PI / 2f);

        var result = map.WorldToScreen(new Vector2(10f, 0f));

        AssertVectorNear(new Vector2(100f, 90f), result, 0.001f);
    }

    [Fact]
    public void Rotated_transform_round_trips_world_coordinates()
    {
        var map = new MapTransform(
            new Vector2(100f, -50f),
            2f,
            new Vector2(800f, 600f),
            RotationRadians: 0.73f);
        var world = new Vector2(-12.5f, 78.25f);

        var result = map.ScreenToWorld(map.WorldToScreen(world));

        AssertVectorNear(world, result, 0.001f);
    }

    [Fact]
    public void ZoomAt_keeps_rotated_world_coordinate_under_cursor()
    {
        var map = new MapTransform(
            new Vector2(100f, -50f),
            2f,
            new Vector2(800f, 600f),
            RotationRadians: -1.17f);
        var cursor = new Vector2(610f, 210f);
        var before = map.ScreenToWorld(cursor);

        var after = map.ZoomAt(cursor, 0.5f);

        AssertVectorNear(before, after.ScreenToWorld(cursor), 0.001f);
        Assert.Equal(map.RotationRadians, after.RotationRadians);
    }

    private static void AssertVectorNear(Vector2 expected, Vector2 actual, float tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }
}
