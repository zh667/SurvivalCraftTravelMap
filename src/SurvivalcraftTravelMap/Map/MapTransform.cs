using System.Numerics;

namespace SurvivalcraftTravelMap.Map;

public readonly record struct MapTransform(Vector2 Center, float BlocksPerPixel, Vector2 ViewportSize)
{
    public Vector2 WorldToScreen(Vector2 world) =>
        ((world - Center) / BlocksPerPixel) + (ViewportSize / 2f);

    public Vector2 ScreenToWorld(Vector2 screen) =>
        Center + ((screen - (ViewportSize / 2f)) * BlocksPerPixel);

    public MapTransform ZoomAt(Vector2 screen, float factor)
    {
        var worldUnderCursor = ScreenToWorld(screen);
        var newBlocksPerPixel = BlocksPerPixel * factor;
        var newCenter = worldUnderCursor -
            ((screen - (ViewportSize / 2f)) * newBlocksPerPixel);
        return new MapTransform(newCenter, newBlocksPerPixel, ViewportSize);
    }
}
