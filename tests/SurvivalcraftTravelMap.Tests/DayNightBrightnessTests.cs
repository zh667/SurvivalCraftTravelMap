using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class DayNightBrightnessTests
{
    [Fact]
    public void Noon_uses_full_brightness()
    {
        Assert.Equal(1f, DayNightBrightness.Calculate(0.5f, 0.4f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void Midnight_uses_minimum_brightness(float timeOfDay)
    {
        Assert.Equal(0.4f, DayNightBrightness.Calculate(timeOfDay, 0.4f));
    }

    [Fact]
    public void Dawn_and_dusk_are_symmetric_transitions()
    {
        var dawn = DayNightBrightness.Calculate(0.25f, 0.4f);
        var dusk = DayNightBrightness.Calculate(0.75f, 0.4f);

        Assert.InRange(dawn, 0.4001f, 0.9999f);
        Assert.Equal(dawn, dusk, precision: 5);
    }

    [Fact]
    public void Dawn_transition_eases_in_and_out()
    {
        var start = DayNightBrightness.Calculate(0.2f, 0.4f);
        var early = DayNightBrightness.Calculate(0.225f, 0.4f);
        var middle = DayNightBrightness.Calculate(0.25f, 0.4f);
        var late = DayNightBrightness.Calculate(0.275f, 0.4f);
        var end = DayNightBrightness.Calculate(0.3f, 0.4f);

        Assert.True(early - start < middle - early);
        Assert.True(end - late < late - middle);
    }
}
