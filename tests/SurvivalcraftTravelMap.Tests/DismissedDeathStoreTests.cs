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
    public async Task Missing_state_file_reports_no_dismissed_deaths()
    {
        var store = new DismissedDeathStore(_directory);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Dismissed);
    }

    [Fact]
    public async Task Dismissed_death_identities_round_trip_through_persistence()
    {
        var first = DeathMarkerIdentity.FromLocation(
            12.5,
            new Vector3(123.5f, -4.25f, 987.75f));
        var second = DeathMarkerIdentity.FromLocation(
            9.0,
            new Vector3(-11f, 64f, 42.5f));

        var store = new DismissedDeathStore(_directory);
        await store.AddAsync(first, TestContext.Current.CancellationToken);
        await store.AddAsync(second, TestContext.Current.CancellationToken);

        Assert.True(store.Contains(first));
        Assert.True(store.Contains(second));

        var reopened = new DismissedDeathStore(_directory);
        await reopened.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new HashSet<DeathMarkerIdentity> { first, second },
            reopened.Dismissed);
        Assert.True(reopened.Contains(first));
        Assert.True(reopened.Contains(second));
    }

    [Fact]
    public async Task Adding_the_same_identity_twice_keeps_a_single_entry()
    {
        var identity = DeathMarkerIdentity.FromLocation(1.0, new Vector3(1f, 2f, 3f));

        var store = new DismissedDeathStore(_directory);
        await store.AddAsync(identity, TestContext.Current.CancellationToken);
        await store.AddAsync(identity, TestContext.Current.CancellationToken);

        var reopened = new DismissedDeathStore(_directory);
        await reopened.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(reopened.Dismissed);
        Assert.True(reopened.Contains(identity));
    }

    [Fact]
    public async Task Corrupt_state_file_is_treated_as_no_dismissed_deaths()
    {
        var path = Path.Combine(_directory, "death-state.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json", TestContext.Current.CancellationToken);

        var store = new DismissedDeathStore(_directory);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Dismissed);
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
