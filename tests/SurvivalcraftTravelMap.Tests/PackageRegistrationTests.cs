using Game.NetWork;
using SurvivalcraftTravelMap.Mod;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class PackageRegistrationTests
{
    [Theory]
    [InlineData(41)]
    [InlineData(61)]
    public void Persistent_startup_failure_keeps_later_xdb_component_inert(int failingId)
    {
        TravelMapStartup.ResetForTests();
        var registrations = new List<byte>();
        var active = TravelMapStartup.EnsureInitialized(
            _ => false,
            package =>
            {
                registrations.Add(package.ID);
                if (package.ID == failingId)
                {
                    throw new InvalidOperationException("conflict");
                }
            },
            _ => { },
            _ => { });
        var laterRegistrations = 0;
        var later = TravelMapStartup.EnsureInitialized(
            _ => false,
            _ => laterRegistrations++,
            _ => { },
            _ => { });

        Assert.False(active);
        Assert.False(later);
        Assert.False(TravelMapStartup.IsActive);
        Assert.Equal(TravelMapStartupState.RegistrationFailed, TravelMapStartup.CurrentState);
        Assert.Equal(0, laterRegistrations);
        TravelMapStartup.ResetForTests();
    }

    [Fact]
    public void Successful_startup_is_idempotent_for_loader_and_component_ordering()
    {
        TravelMapStartup.ResetForTests();
        var registrations = 0;

        Assert.True(TravelMapStartup.EnsureInitialized(_ => false, _ => registrations++, _ => { }, _ => { }));
        Assert.True(TravelMapStartup.EnsureInitialized(_ => false, _ => registrations++, _ => { }, _ => { }));

        Assert.True(TravelMapStartup.IsActive);
        Assert.Equal(2, registrations);
        TravelMapStartup.ResetForTests();
    }

    [Fact]
    public void Startup_checks_only_34gpsfix_and_does_not_partially_register_on_conflict()
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
            _ => throw new InvalidOperationException("unexpected rollback"),
            messages.Add);

        Assert.False(success);
        Assert.Equal(["34GPSFix"], checkedPackages);
        Assert.Empty(registered);
        var message = Assert.Single(messages);
        Assert.Contains("34GPSFix", message, StringComparison.Ordinal);
        Assert.Contains("restart", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_registers_exactly_41_and_61_when_legacy_mod_is_absent()
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
            _ => throw new InvalidOperationException("unexpected rollback"),
            _ => throw new InvalidOperationException("unexpected error"));

        Assert.True(success);
        Assert.Equal(["34GPSFix"], checkedPackages);
        Assert.Equal(new byte[] { 41, 61 }, registered);
    }

    [Fact]
    public void Registration_installs_only_41_and_61()
    {
        var registered = new List<byte>();

        var success = TravelMapPackageRegistration.TryRegister(
            package => registered.Add(package.ID),
            _ => throw new InvalidOperationException("unexpected rollback"),
            _ => throw new InvalidOperationException("unexpected error"));

        Assert.True(success);
        Assert.Equal(new byte[] { 41, 61 }, registered);
        Assert.DoesNotContain((byte)60, registered);
    }

    [Fact]
    public void Registration_rolls_back_41_if_61_conflicts_and_reports_the_id()
    {
        var registered = new List<byte>();
        var unregistered = new List<byte>();
        var errors = new List<string>();

        var success = TravelMapPackageRegistration.TryRegister(
            package =>
            {
                if (package.ID == 61)
                {
                    throw new InvalidOperationException("duplicate package");
                }

                registered.Add(package.ID);
            },
            package => unregistered.Add(package.ID),
            errors.Add);

        Assert.False(success);
        Assert.Equal(new byte[] { 41 }, registered);
        Assert.Equal(new byte[] { 41 }, unregistered);
        Assert.Single(errors);
        Assert.Contains("61", errors[0], StringComparison.Ordinal);
    }
}
