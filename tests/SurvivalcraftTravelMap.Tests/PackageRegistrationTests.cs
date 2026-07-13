using Game.NetWork;
using SurvivalcraftTravelMap.Mod;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class PackageRegistrationTests
{
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
