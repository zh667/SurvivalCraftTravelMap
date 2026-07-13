namespace SurvivalcraftTravelMap.Settings;

public sealed class TravelMapSettings
{
    private static readonly int[] SupportedMiniMapSizes = [160, 192, 256, 320, 384];

    public bool IsMiniMapVisible { get; set; } = true;

    public bool ShowCoordinates { get; set; } = true;

    public bool UseDayNightTint { get; set; } = true;

    public bool AcceptTeleportInvitations { get; set; } = true;

    public int MiniMapSize { get; set; } = 256;

    public float MiniMapBlocksPerPixel { get; set; } = 1f;

    public float LargeMapBlocksPerPixel { get; set; } = 2f;

    public string LargeMapHotkey { get; set; } = "M";

    public float NightMinimumBrightness { get; set; } = 0.4f;

    public void Normalize()
    {
        MiniMapSize = NearestSupportedSize(MiniMapSize);
        MiniMapBlocksPerPixel = ClampOrDefault(MiniMapBlocksPerPixel, 0.5f, 8f, 1f);
        LargeMapBlocksPerPixel = ClampOrDefault(LargeMapBlocksPerPixel, 0.25f, 32f, 2f);
        NightMinimumBrightness = ClampOrDefault(NightMinimumBrightness, 0.4f, 1f, 0.4f);
    }

    private static int NearestSupportedSize(int requested)
    {
        var nearest = SupportedMiniMapSizes[0];
        var nearestDistance = Math.Abs((long)requested - nearest);

        foreach (var candidate in SupportedMiniMapSizes.AsSpan(1))
        {
            var distance = Math.Abs((long)requested - candidate);
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static float ClampOrDefault(float value, float minimum, float maximum, float fallback) =>
        float.IsNaN(value) ? fallback : Math.Clamp(value, minimum, maximum);
}
