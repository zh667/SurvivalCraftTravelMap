using System.Numerics;

namespace SurvivalcraftTravelMap.Map;

public readonly record struct MapTransform(
    Vector2 Center,
    float BlocksPerPixel,
    Vector2 ViewportSize,
    float RotationRadians = 0f)
{
    // The map plane is rotated 180 degrees from raw Survivalcraft world (X, Z). In-game the sun
    // rises toward -X and sets toward +X, so the real (sun-based) cardinal directions are
    // East = -X, West = +X, North = +Z, South = -Z -- exactly opposite to the block-face normals
    // (which call +X "east", -Z "north"). Mapping the plane as (Center - world) instead of
    // (world - Center) puts +Z (north) up and -X (east) to the right, so the minimap matches the
    // sun and the player's sense of direction. See MapTransformTests / GetPlayerPose (heading is
    // rotated 180 degrees to match).
    public Vector2 WorldToScreen(Vector2 world) =>
        Rotate(Center - world, RotationRadians) / BlocksPerPixel + (ViewportSize / 2f);

    public Vector2 ScreenToWorld(Vector2 screen) =>
        Center - Rotate(
            (screen - (ViewportSize / 2f)) * BlocksPerPixel,
            -RotationRadians);

    public MapTransform ZoomAt(Vector2 screen, float factor)
    {
        var worldUnderCursor = ScreenToWorld(screen);
        var newBlocksPerPixel = BlocksPerPixel * factor;
        var worldOffset = Rotate(
            (screen - (ViewportSize / 2f)) * newBlocksPerPixel,
            -RotationRadians);
        var newCenter = worldUnderCursor + worldOffset;
        return new MapTransform(newCenter, newBlocksPerPixel, ViewportSize, RotationRadians);
    }

    private static Vector2 Rotate(Vector2 value, float radians)
    {
        if (radians == 0f)
        {
            return value;
        }

        var sine = MathF.Sin(radians);
        var cosine = MathF.Cos(radians);
        return new Vector2(
            (value.X * cosine) - (value.Y * sine),
            (value.X * sine) + (value.Y * cosine));
    }
}
