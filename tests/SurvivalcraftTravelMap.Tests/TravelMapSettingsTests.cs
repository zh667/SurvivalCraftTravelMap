using SurvivalcraftTravelMap.Settings;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapSettingsTests
{
    [Fact]
    public void Defaults_match_the_design()
    {
        var settings = TravelMapSettings.CreateDefaults();

        Assert.True(settings.IsMiniMapVisible);
        Assert.True(settings.ShowCoordinates);
        Assert.True(settings.UseDayNightTint);
        Assert.True(settings.UseHeightShading);
        Assert.True(settings.AcceptTeleportInvitations);
        Assert.True(settings.ShowCreatureMarkers);
        Assert.True(settings.ShowLastDeathMarker);
        Assert.Equal(5f, settings.CreatureMarkerSize);
        Assert.Equal(MiniMapOrientation.NorthUp, settings.MiniMapOrientation);
        Assert.True(settings.ShowCompassNorth);
        Assert.True(settings.ShowCompassOtherDirections);
        Assert.Equal(1f, settings.CompassFontScale);
        Assert.Equal(MapShape.RoundedSquare, settings.MiniMapShape);
        Assert.True(settings.ShowGameTime);
        Assert.Equal(160, settings.MiniMapSize);
        Assert.Equal(1.0f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(2.0f, settings.LargeMapBlocksPerPixel);
        Assert.Equal("M", settings.LargeMapHotkey);
        Assert.Equal(0.4f, settings.NightMinimumBrightness);
    }

    [Fact]
    public void Reset_presentation_restores_defaults_without_changing_invitation_consent()
    {
        var settings = new TravelMapSettings
        {
            IsMiniMapVisible = false,
            ShowCoordinates = false,
            UseDayNightTint = false,
            UseHeightShading = false,
            AcceptTeleportInvitations = false,
            ShowCreatureMarkers = false,
            ShowLastDeathMarker = false,
            CreatureMarkerSize = 14f,
            MiniMapOrientation = MiniMapOrientation.HeadingUp,
            ShowCompassNorth = false,
            ShowCompassOtherDirections = false,
            CompassFontScale = 1.8f,
            MiniMapAnchorX = 0.2f,
            MiniMapAnchorY = 0.8f,
            MiniMapShape = MapShape.Circle,
            ShowGameTime = false,
            MiniMapSize = 384,
            MiniMapBlocksPerPixel = 7f,
            LargeMapBlocksPerPixel = 18f,
            NightMinimumBrightness = 0.9f,
        };

        settings.ResetPresentationToDefaults();

        var defaults = TravelMapSettings.CreateDefaults();
        Assert.False(settings.AcceptTeleportInvitations);
        Assert.Equal(defaults.IsMiniMapVisible, settings.IsMiniMapVisible);
        Assert.Equal(defaults.ShowCoordinates, settings.ShowCoordinates);
        Assert.Equal(defaults.UseDayNightTint, settings.UseDayNightTint);
        Assert.Equal(defaults.UseHeightShading, settings.UseHeightShading);
        Assert.Equal(defaults.ShowCreatureMarkers, settings.ShowCreatureMarkers);
        Assert.Equal(defaults.ShowLastDeathMarker, settings.ShowLastDeathMarker);
        Assert.Equal(defaults.CreatureMarkerSize, settings.CreatureMarkerSize);
        Assert.Equal(defaults.MiniMapOrientation, settings.MiniMapOrientation);
        Assert.Equal(defaults.ShowCompassNorth, settings.ShowCompassNorth);
        Assert.Equal(defaults.ShowCompassOtherDirections, settings.ShowCompassOtherDirections);
        Assert.Equal(defaults.CompassFontScale, settings.CompassFontScale);
        Assert.Equal(defaults.MiniMapAnchorX, settings.MiniMapAnchorX);
        Assert.Equal(defaults.MiniMapAnchorY, settings.MiniMapAnchorY);
        Assert.Equal(defaults.MiniMapShape, settings.MiniMapShape);
        Assert.Equal(defaults.ShowGameTime, settings.ShowGameTime);
        Assert.Equal(defaults.MiniMapSize, settings.MiniMapSize);
        Assert.Equal(defaults.MiniMapBlocksPerPixel, settings.MiniMapBlocksPerPixel);
        Assert.Equal(defaults.LargeMapBlocksPerPixel, settings.LargeMapBlocksPerPixel);
        Assert.Equal(defaults.NightMinimumBrightness, settings.NightMinimumBrightness);
    }

    [Fact]
    public void Normalize_clears_incomplete_or_non_finite_custom_minimap_anchor()
    {
        var settings = new TravelMapSettings
        {
            MiniMapAnchorX = 0.25f,
            MiniMapAnchorY = float.NaN,
        };

        settings.Normalize();

        Assert.Null(settings.MiniMapAnchorX);
        Assert.Null(settings.MiniMapAnchorY);
    }

    [Fact]
    public void Normalize_clamps_complete_custom_minimap_anchor()
    {
        var settings = new TravelMapSettings
        {
            MiniMapAnchorX = -2f,
            MiniMapAnchorY = 3f,
        };

        settings.Normalize();

        Assert.Equal(0f, settings.MiniMapAnchorX);
        Assert.Equal(1f, settings.MiniMapAnchorY);
    }

    [Fact]
    public void Normalize_restores_unknown_minimap_orientation_to_north_up()
    {
        var settings = new TravelMapSettings
        {
            MiniMapOrientation = (MiniMapOrientation)999,
        };

        settings.Normalize();

        Assert.Equal(MiniMapOrientation.NorthUp, settings.MiniMapOrientation);
    }

    [Fact]
    public void Normalize_restores_unknown_map_shape_to_rounded_square()
    {
        var settings = new TravelMapSettings
        {
            MiniMapShape = (MapShape)999,
        };

        settings.Normalize();

        Assert.Equal(MapShape.RoundedSquare, settings.MiniMapShape);
    }

    [Theory]
    [InlineData(100, 160)]
    [InlineData(205, 192)]
    [InlineData(300, 320)]
    [InlineData(500, 384)]
    public void Normalize_uses_the_nearest_supported_minimap_size(int requested, int expected)
    {
        var settings = new TravelMapSettings { MiniMapSize = requested };

        settings.Normalize();

        Assert.Equal(expected, settings.MiniMapSize);
    }

    [Fact]
    public void Normalize_clamps_numeric_values_to_supported_ranges()
    {
        var settings = new TravelMapSettings
        {
            MiniMapBlocksPerPixel = -1f,
            LargeMapBlocksPerPixel = 100f,
            NightMinimumBrightness = 2f,
            CreatureMarkerSize = 100f,
            CompassFontScale = 100f,
        };

        settings.Normalize();

        Assert.Equal(0.5f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(32f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(1f, settings.NightMinimumBrightness);
        Assert.Equal(16f, settings.CreatureMarkerSize);
        Assert.Equal(2f, settings.CompassFontScale);
    }

    [Fact]
    public void Normalize_restores_non_numeric_values_to_defaults()
    {
        var settings = new TravelMapSettings
        {
            MiniMapBlocksPerPixel = float.NaN,
            LargeMapBlocksPerPixel = float.NaN,
            NightMinimumBrightness = float.NaN,
            CreatureMarkerSize = float.NaN,
            CompassFontScale = float.NaN,
        };

        settings.Normalize();

        Assert.Equal(1f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(2f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(0.4f, settings.NightMinimumBrightness);
        Assert.Equal(5f, settings.CreatureMarkerSize);
        Assert.Equal(1f, settings.CompassFontScale);
    }
}
