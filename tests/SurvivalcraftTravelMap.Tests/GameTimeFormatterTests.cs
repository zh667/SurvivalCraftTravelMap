using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class GameTimeFormatterTests
{
    [Theory]
    [InlineData(0f, "00:00")]
    [InlineData(0.5f, "12:00")]
    [InlineData(1f, "00:00")]
    [InlineData(1.5f, "12:00")]
    public void Formats_normalized_time_as_twenty_four_hour_clock(float timeOfDay, string expected)
    {
        Assert.Equal(expected, GameTimeFormatter.Format(timeOfDay));
    }

    [Fact]
    public void Last_minute_before_day_wrap_is_2359()
    {
        Assert.Equal("23:59", GameTimeFormatter.FormatMinute(1439));
        Assert.Equal("00:00", GameTimeFormatter.FormatMinute(1440));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Invalid_time_uses_safe_midnight_fallback(float timeOfDay)
    {
        Assert.Equal("00:00", GameTimeFormatter.Format(timeOfDay));
    }
}
