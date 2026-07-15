using SurvivalcraftTravelMap.Settings;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapSettingsTests
{
    [Fact]
    public void Defaults_match_the_design()
    {
        var settings = new TravelMapSettings();

        Assert.True(settings.IsMiniMapVisible);
        Assert.True(settings.ShowCoordinates);
        Assert.True(settings.UseDayNightTint);
        Assert.True(settings.AcceptTeleportInvitations);
        Assert.True(settings.ShowCreatureMarkers);
        Assert.Equal(5f, settings.CreatureMarkerSize);
        Assert.Equal(MiniMapOrientation.NorthUp, settings.MiniMapOrientation);
        Assert.Equal(160, settings.MiniMapSize);
        Assert.Equal(1.0f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(2.0f, settings.LargeMapBlocksPerPixel);
        Assert.Equal("M", settings.LargeMapHotkey);
        Assert.Equal(0.4f, settings.NightMinimumBrightness);
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
        };

        settings.Normalize();

        Assert.Equal(0.5f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(32f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(1f, settings.NightMinimumBrightness);
        Assert.Equal(16f, settings.CreatureMarkerSize);
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
        };

        settings.Normalize();

        Assert.Equal(1f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(2f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(0.4f, settings.NightMinimumBrightness);
        Assert.Equal(5f, settings.CreatureMarkerSize);
    }
}
