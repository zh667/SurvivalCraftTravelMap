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
        Assert.Equal(256, settings.MiniMapSize);
        Assert.Equal(1.0f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(2.0f, settings.LargeMapBlocksPerPixel);
        Assert.Equal("M", settings.LargeMapHotkey);
        Assert.Equal(0.4f, settings.NightMinimumBrightness);
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
        };

        settings.Normalize();

        Assert.Equal(0.5f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(32f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(1f, settings.NightMinimumBrightness);
    }

    [Fact]
    public void Normalize_restores_non_numeric_values_to_defaults()
    {
        var settings = new TravelMapSettings
        {
            MiniMapBlocksPerPixel = float.NaN,
            LargeMapBlocksPerPixel = float.NaN,
            NightMinimumBrightness = float.NaN,
        };

        settings.Normalize();

        Assert.Equal(1f, settings.MiniMapBlocksPerPixel);
        Assert.Equal(2f, settings.LargeMapBlocksPerPixel);
        Assert.Equal(0.4f, settings.NightMinimumBrightness);
    }
}
