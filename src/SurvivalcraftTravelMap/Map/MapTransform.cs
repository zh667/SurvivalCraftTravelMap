using System.Numerics;

namespace SurvivalcraftTravelMap.Map;

public readonly record struct MapTransform(
    Vector2 Center,
    float BlocksPerPixel,
    Vector2 ViewportSize,
    float RotationRadians = 0f)
{
    public Vector2 WorldToScreen(Vector2 world) =>
        Rotate(world - Center, RotationRadians) / BlocksPerPixel + (ViewportSize / 2f);

    public Vector2 ScreenToWorld(Vector2 screen) =>
        Center + Rotate(
            (screen - (ViewportSize / 2f)) * BlocksPerPixel,
            -RotationRadians);

    public MapTransform ZoomAt(Vector2 screen, float factor)
    {
        var worldUnderCursor = ScreenToWorld(screen);
        var newBlocksPerPixel = BlocksPerPixel * factor;
        var worldOffset = Rotate(
            (screen - (ViewportSize / 2f)) * newBlocksPerPixel,
            -RotationRadians);
        var newCenter = worldUnderCursor - worldOffset;
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
