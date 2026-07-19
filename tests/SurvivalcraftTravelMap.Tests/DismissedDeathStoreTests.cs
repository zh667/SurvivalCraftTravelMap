using System.Numerics;
using SurvivalcraftTravelMap.Waypoints;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class DismissedDeathStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "SurvivalcraftTravelMap.Tests",
        Guid.NewGuid().ToString("N"));

    public DismissedDeathStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Missing_state_file_reports_no_dismissed_death()
    {
        var store = new DismissedDeathStore(_directory);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(store.Dismissed);
    }

    [Fact]
    public async Task Dismissed_death_identity_round_trips_through_persistence()
    {
        var identity = DeathMarkerIdentity.FromLocation(
            12.5,
            new Vector3(123.5f, -4.25f, 987.75f));

        var store = new DismissedDeathStore(_directory);
        await store.SetAsync(identity, TestContext.Current.CancellationToken);

        var reopened = new DismissedDeathStore(_directory);
        await reopened.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(identity, reopened.Dismissed);
    }

    [Fact]
    public async Task Clearing_the_dismissed_death_persists_null()
    {
        var store = new DismissedDeathStore(_directory);
        await store.SetAsync(
            DeathMarkerIdentity.FromLocation(1.0, new Vector3(1f, 2f, 3f)),
            TestContext.Current.CancellationToken);
        await store.SetAsync(null, TestContext.Current.CancellationToken);

        var reopened = new DismissedDeathStore(_directory);
        await reopened.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(reopened.Dismissed);
    }

    [Fact]
    public void Identity_serialization_is_exact_and_parseable()
    {
        var identity = DeathMarkerIdentity.FromLocation(
            3.25,
            new Vector3(40f, 50.5f, -60.125f));

        Assert.True(DeathMarkerIdentity.TryParse(identity.Serialize(), out var parsed));
        Assert.Equal(identity, parsed);
        Assert.False(DeathMarkerIdentity.TryParse("not,an,identity", out _));
        Assert.False(DeathMarkerIdentity.TryParse(null, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
