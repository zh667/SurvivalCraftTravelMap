using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class WorldCacheSeedGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "SurvivalcraftTravelMap.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void First_use_stamps_the_seed_and_keeps_the_directory()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "tile.dat"), "explored");

        var outcome = WorldCacheSeedGuard.Ensure(_directory, worldSeed: 12345);

        Assert.Equal(WorldCacheSeedGuard.GuardOutcome.Matched, outcome);
        Assert.True(File.Exists(Path.Combine(_directory, "tile.dat")));
        Assert.Equal("12345", File.ReadAllText(Path.Combine(_directory, "world-seed.txt")));
    }

    [Fact]
    public void Reopening_the_same_world_keeps_the_cache()
    {
        WorldCacheSeedGuard.Ensure(_directory, worldSeed: 777);
        File.WriteAllText(Path.Combine(_directory, "tile.dat"), "explored");

        var outcome = WorldCacheSeedGuard.Ensure(_directory, worldSeed: 777);

        Assert.Equal(WorldCacheSeedGuard.GuardOutcome.Matched, outcome);
        Assert.True(File.Exists(Path.Combine(_directory, "tile.dat")));
    }

    [Fact]
    public void Reused_directory_name_with_a_different_seed_clears_the_stale_cache()
    {
        WorldCacheSeedGuard.Ensure(_directory, worldSeed: 111);
        File.WriteAllText(Path.Combine(_directory, "tile.dat"), "old world terrain");

        var outcome = WorldCacheSeedGuard.Ensure(_directory, worldSeed: 222);

        Assert.Equal(WorldCacheSeedGuard.GuardOutcome.Reset, outcome);
        Assert.False(File.Exists(Path.Combine(_directory, "tile.dat")));
        Assert.Equal("222", File.ReadAllText(Path.Combine(_directory, "world-seed.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
