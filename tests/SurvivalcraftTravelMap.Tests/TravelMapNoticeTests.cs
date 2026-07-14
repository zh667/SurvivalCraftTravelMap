using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapNoticeTests
{
    [Fact]
    public void Show_replaces_the_current_notice_and_refreshes_expiry()
    {
        var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));
        controller.Show(new TravelMapNotice("first", TravelMapNoticeKind.Information), 10d);
        controller.Show(new TravelMapNotice("second", TravelMapNoticeKind.Failure), 11d);

        Assert.Equal("second", controller.Current?.Text);
        Assert.Equal(TravelMapNoticeKind.Failure, controller.Current?.Kind);
        Assert.True(controller.Update(13.49d));
        Assert.False(controller.Update(13.5d));
        Assert.Null(controller.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_notices_are_rejected(string text)
    {
        var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));

        Assert.Throws<ArgumentException>(() =>
            controller.Show(new TravelMapNotice(text, TravelMapNoticeKind.Information), 0d));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.001d)]
    public void Non_positive_durations_are_rejected(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TravelMapNoticeController(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Show_rejects_non_finite_timestamps(double now)
    {
        var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.Show(new TravelMapNotice("notice", TravelMapNoticeKind.Success), now));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Update_rejects_non_finite_timestamps(double now)
    {
        var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));

        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Update(now));
    }

    [Fact]
    public void Clear_immediately_removes_the_current_notice()
    {
        var controller = new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));
        controller.Show(new TravelMapNotice("notice", TravelMapNoticeKind.Information), 10d);

        controller.Clear();

        Assert.Null(controller.Current);
        Assert.False(controller.Update(10d));
    }
}
