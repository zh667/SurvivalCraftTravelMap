namespace SurvivalcraftTravelMap.Settings;

public enum MiniMapOrientation
{
    NorthUp,
    HeadingUp,
}

public enum MapShape
{
    Circle,
    Square,
    RoundedSquare,
}

public enum HeightShadingStyle
{
    Off,
    Soft,
    Standard,
    HighContrast,
}

public static class HeightShadingStyleExtensions
{
    /// <summary>
    /// Render-time contrast multiplier applied to each pixel's baked shade factor
    /// around neutral (1.0). 0 flattens the relief, 1 keeps the baked look, and
    /// larger values exaggerate it. Applied on top of the stored byte, so switching
    /// styles restyles already-explored terrain immediately.
    /// </summary>
    public static float ToStrength(this HeightShadingStyle style) => style switch
    {
        HeightShadingStyle.Off => 0f,
        HeightShadingStyle.Soft => 0.6f,
        HeightShadingStyle.Standard => 1f,
        HeightShadingStyle.HighContrast => 1.7f,
        _ => 1f,
    };
}

public sealed class TravelMapSettings
{
    private static readonly int[] MiniMapSizes = [160, 192, 256, 320, 384];

    public static IReadOnlyList<int> SupportedMiniMapSizes { get; } = Array.AsReadOnly(MiniMapSizes);

    public bool IsMiniMapVisible { get; set; } = true;

    public bool ShowCoordinates { get; set; } = true;

    public bool UseDayNightTint { get; set; } = true;

    public HeightShadingStyle HeightShadingStyle { get; set; } = HeightShadingStyle.Standard;

    public bool AcceptTeleportInvitations { get; set; } = true;

    public bool ShowCreatureMarkers { get; set; } = true;

    public bool ShowLastDeathMarker { get; set; } = true;

    public float CreatureMarkerSize { get; set; } = 5f;

    public MiniMapOrientation MiniMapOrientation { get; set; } = MiniMapOrientation.NorthUp;

    public bool ShowCompassNorth { get; set; } = true;

    public bool ShowCompassOtherDirections { get; set; } = true;

    public float CompassFontScale { get; set; } = 1f;

    public float? MiniMapAnchorX { get; set; }

    public float? MiniMapAnchorY { get; set; }

    public MapShape MiniMapShape { get; set; } = MapShape.RoundedSquare;

    public bool ShowGameTime { get; set; } = true;

    public int MiniMapSize { get; set; } = 160;

    public float MiniMapBlocksPerPixel { get; set; } = 1f;

    public float LargeMapBlocksPerPixel { get; set; } = 2f;

    public string LargeMapHotkey { get; set; } = "M";

    public float NightMinimumBrightness { get; set; } = 0.4f;

    public static TravelMapSettings CreateDefaults()
    {
        var settings = new TravelMapSettings();
        settings.Normalize();
        return settings;
    }

    public void ResetPresentationToDefaults()
    {
        var defaults = CreateDefaults();
        IsMiniMapVisible = defaults.IsMiniMapVisible;
        ShowCoordinates = defaults.ShowCoordinates;
        UseDayNightTint = defaults.UseDayNightTint;
        HeightShadingStyle = defaults.HeightShadingStyle;
        ShowCreatureMarkers = defaults.ShowCreatureMarkers;
        ShowLastDeathMarker = defaults.ShowLastDeathMarker;
        CreatureMarkerSize = defaults.CreatureMarkerSize;
        MiniMapOrientation = defaults.MiniMapOrientation;
        ShowCompassNorth = defaults.ShowCompassNorth;
        ShowCompassOtherDirections = defaults.ShowCompassOtherDirections;
        CompassFontScale = defaults.CompassFontScale;
        MiniMapAnchorX = defaults.MiniMapAnchorX;
        MiniMapAnchorY = defaults.MiniMapAnchorY;
        MiniMapShape = defaults.MiniMapShape;
        ShowGameTime = defaults.ShowGameTime;
        MiniMapSize = defaults.MiniMapSize;
        MiniMapBlocksPerPixel = defaults.MiniMapBlocksPerPixel;
        LargeMapBlocksPerPixel = defaults.LargeMapBlocksPerPixel;
        LargeMapHotkey = defaults.LargeMapHotkey;
        NightMinimumBrightness = defaults.NightMinimumBrightness;
    }

    public void Normalize()
    {
        MiniMapSize = NearestSupportedSize(MiniMapSize);
        MiniMapBlocksPerPixel = ClampOrDefault(MiniMapBlocksPerPixel, 0.5f, 8f, 1f);
        LargeMapBlocksPerPixel = ClampOrDefault(LargeMapBlocksPerPixel, 0.25f, 32f, 2f);
        NightMinimumBrightness = ClampOrDefault(NightMinimumBrightness, 0.4f, 1f, 0.4f);
        CreatureMarkerSize = ClampOrDefault(CreatureMarkerSize, 3f, 16f, 5f);
        CompassFontScale = ClampOrDefault(CompassFontScale, 0.5f, 2f, 1f);
        NormalizeMiniMapAnchor();
        if (!Enum.IsDefined(MiniMapOrientation))
        {
            MiniMapOrientation = MiniMapOrientation.NorthUp;
        }

        if (!Enum.IsDefined(MiniMapShape))
        {
            MiniMapShape = MapShape.RoundedSquare;
        }

        if (!Enum.IsDefined(HeightShadingStyle))
        {
            HeightShadingStyle = HeightShadingStyle.Standard;
        }

        LargeMapHotkey = "M";
    }

    public static bool IsSupportedMiniMapSize(int size) => Array.IndexOf(MiniMapSizes, size) >= 0;

    private static int NearestSupportedSize(int requested)
    {
        var nearest = MiniMapSizes[0];
        var nearestDistance = Math.Abs((long)requested - nearest);

        foreach (var candidate in MiniMapSizes.AsSpan(1))
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

    private void NormalizeMiniMapAnchor()
    {
        if (!MiniMapAnchorX.HasValue
            || !MiniMapAnchorY.HasValue
            || !float.IsFinite(MiniMapAnchorX.Value)
            || !float.IsFinite(MiniMapAnchorY.Value))
        {
            MiniMapAnchorX = null;
            MiniMapAnchorY = null;
            return;
        }

        MiniMapAnchorX = Math.Clamp(MiniMapAnchorX.Value, 0f, 1f);
        MiniMapAnchorY = Math.Clamp(MiniMapAnchorY.Value, 0f, 1f);
    }
}
