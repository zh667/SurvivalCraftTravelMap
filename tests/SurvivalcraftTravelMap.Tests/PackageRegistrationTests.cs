using Game.NetWork;
using SurvivalcraftTravelMap.Mod;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class PackageRegistrationTests
{
    [Theory]
    [InlineData(41, false, true)]
    [InlineData(217, true, false)]
    public void Package_conflict_degrades_only_that_feature_and_keeps_the_mod_active(
        int failingId,
        bool expectLegacyGps,
        bool expectCoordinateTeleport)
    {
        TravelMapStartup.ResetForTests();
        var warnings = new List<string>();
        var active = TravelMapStartup.EnsureInitialized(
            _ => false,
            package =>
            {
                if (package.ID == failingId)
                {
                    throw new InvalidOperationException("conflict");
                }
            },
            _ => throw new InvalidOperationException("unexpected error dialog"),
            warnings.Add);

        Assert.True(active);
        Assert.True(TravelMapStartup.IsActive);
        Assert.Equal(TravelMapStartupState.Active, TravelMapStartup.CurrentState);
        Assert.Equal(expectLegacyGps, TravelMapStartup.IsLegacyGpsAvailable);
        Assert.Equal(expectCoordinateTeleport, TravelMapStartup.IsCoordinateTeleportAvailable);
        var warning = Assert.Single(warnings);
        Assert.Contains(failingId.ToString(), warning, StringComparison.Ordinal);
        Assert.Contains("map itself is unaffected", warning, StringComparison.Ordinal);
        TravelMapStartup.ResetForTests();
    }

    [Fact]
    public void Successful_startup_is_idempotent_for_loader_and_component_ordering()
    {
        TravelMapStartup.ResetForTests();
        var registrations = 0;

        Assert.True(TravelMapStartup.EnsureInitialized(
            _ => false,
            _ => registrations++,
            _ => { },
            _ => { }));
        Assert.True(TravelMapStartup.EnsureInitialized(
            _ => false,
            _ => registrations++,
            _ => { },
            _ => { }));

        Assert.True(TravelMapStartup.IsActive);
        Assert.True(TravelMapStartup.IsLegacyGpsAvailable);
        Assert.True(TravelMapStartup.IsCoordinateTeleportAvailable);
        Assert.Equal(2, registrations);
        TravelMapStartup.ResetForTests();
    }

    [Fact]
    public void Startup_checks_only_34gpsfix_and_registers_nothing_on_legacy_conflict()
    {
        var checkedPackages = new List<string>();
        var registered = new List<byte>();
        var messages = new List<string>();

        var success = TravelMapStartup.TryInitialize(
            packageName =>
            {
                checkedPackages.Add(packageName);
                return true;
            },
            package => registered.Add(package.ID),
            messages.Add,
            _ => throw new InvalidOperationException("unexpected warning"),
            out var packages);

        Assert.False(success);
        Assert.Equal(default, packages);
        Assert.Equal(["34GPSFix"], checkedPackages);
        Assert.Empty(registered);
        var message = Assert.Single(messages);
        Assert.Contains("34GPSFix", message, StringComparison.Ordinal);
        Assert.Contains("restart", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_registers_exactly_41_and_217_when_legacy_mod_is_absent()
    {
        var checkedPackages = new List<string>();
        var registered = new List<byte>();

        var success = TravelMapStartup.TryInitialize(
            packageName =>
            {
                checkedPackages.Add(packageName);
                return false;
            },
            package => registered.Add(package.ID),
            _ => throw new InvalidOperationException("unexpected error"),
            _ => throw new InvalidOperationException("unexpected warning"),
            out var packages);

        Assert.True(success);
        Assert.Equal(new TravelMapPackageAvailability(true, true), packages);
        Assert.Equal(["34GPSFix"], checkedPackages);
        Assert.Equal(new byte[] { 41, 217 }, registered);
    }

    [Fact]
    public void Coordinate_teleport_id_avoids_vanilla_and_known_conflicting_ranges()
    {
        // Vanilla uses 0-40, 56-59 and 250-253; server-side anticheat mods are known to claim 61
        // (the original coordinate-teleport ID). Both packages must stay clear of all of those.
        byte[] claimed = [41, 217];
        var forbidden = Enumerable.Range(0, 41)
            .Concat(Enumerable.Range(56, 4))
            .Concat(Enumerable.Range(250, 4))
            .Concat([61])
            .ToHashSet();

        Assert.Equal(41, claimed[0]);
        Assert.DoesNotContain(claimed[1], forbidden.Select(id => (byte)id));
        var registered = new List<byte>();
        TravelMapPackageRegistration.TryRegister(
            package => registered.Add(package.ID),
            _ => throw new InvalidOperationException("unexpected warning"));
        Assert.Equal(claimed, registered);
    }

    [Fact]
    public void Registration_keeps_41_when_217_conflicts_and_reports_the_id()
    {
        var registered = new List<byte>();
        var warnings = new List<string>();

        var packages = TravelMapPackageRegistration.TryRegister(
            package =>
            {
                if (package.ID == 217)
                {
                    throw new InvalidOperationException("duplicate package");
                }

                registered.Add(package.ID);
            },
            warnings.Add);

        Assert.Equal(new TravelMapPackageAvailability(LegacyGps: true, CoordinateTeleport: false), packages);
        Assert.Equal(new byte[] { 41 }, registered);
        var warning = Assert.Single(warnings);
        Assert.Contains("217", warning, StringComparison.Ordinal);
        Assert.Contains("online map teleport", warning, StringComparison.Ordinal);
    }
}
