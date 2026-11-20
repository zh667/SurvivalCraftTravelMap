using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

/// <summary>
/// The map cache used to be keyed by the game's world directory name (app:/Worlds/World1), which
/// does not include the install: two installs (or a test copy) with a same-named world shared one
/// cache, and the seed guard wiped it whenever the other one was opened. Local worlds are now keyed
/// by the world folder's full system path; the old cache is copied over when its seed matches.
/// </summary>
public sealed class WorldCachePathKeyTests
{
    private static readonly Guid Player = Guid.Parse("7b570f99-8a40-40bd-9685-522f5b3ec3fc");

    private static TravelMapStorageIdentityInput Local(string appRoot, string? systemPath) => new(
        TravelMapStorageScope.LocalWorld,
        appRoot,
        "app:/Worlds/World1",
        null,
        null,
        "app:/Worlds/World1",
        Player,
        LocalWorldSystemPath: systemPath);

    [Fact]
    public void Local_world_is_keyed_by_its_full_system_path()
    {
        Assert.True(TravelMapStorageIdentity.TryResolve(Local("/data", "/games/sc-a/Worlds/World1"), out var location, out _));

        Assert.Equal(WorldKey.ForLocalPath("/games/sc-a/Worlds/World1"), location!.WorldKey);
        Assert.Equal(
            Path.GetFullPath(Path.Combine("/data", "maps", WorldKey.ForLocalPath("/games/sc-a/Worlds/World1"), Player.ToString("N"))),
            location.Directory);
        Assert.Equal(
            Path.GetFullPath(Path.Combine("/data", "maps", WorldKey.ForLocal("app:/Worlds/World1"), Player.ToString("N"))),
            location.LegacyDirectory);
    }

    [Fact]
    public void Same_named_worlds_of_two_installs_get_different_keys()
    {
        Assert.NotEqual(
            WorldKey.ForLocalPath("/games/sc-a/Worlds/World1"),
            WorldKey.ForLocalPath("/games/sc-b/Worlds/World1"));
        Assert.NotEqual(WorldKey.ForLocal("app:/Worlds/World1"), WorldKey.ForLocalPath("/games/sc-a/Worlds/World1"));
    }

    [Theory]
    [InlineData("C:\\Games\\SC\\Worlds\\World1", "c:/games/sc/worlds/world1/")]
    [InlineData("C:/Games/SC/Worlds/World1\\", "C:\\GAMES\\SC\\WORLDS\\WORLD1")]
    [InlineData("/games/sc/Worlds/World1", "/games/sc/worlds/world1//")]
    public void Path_key_ignores_separator_style_trailing_separators_and_case(string left, string right)
    {
        Assert.Equal(WorldKey.ForLocalPath(left), WorldKey.ForLocalPath(right));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_system_path_the_old_key_is_used(string? systemPath)
    {
        Assert.True(TravelMapStorageIdentity.TryResolve(Local("/data", systemPath), out var location, out _));

        Assert.Equal(WorldKey.ForLocal("app:/Worlds/World1"), location!.WorldKey);
        Assert.Null(location.LegacyDirectory);
    }

    [Fact]
    public void Old_cache_with_the_same_seed_is_copied_and_kept()
    {
        using var root = new TemporaryDirectory();
        var (current, legacy) = Directories(root.Path);
        WriteCache(legacy, seed: 42);

        var outcome = WorldCacheMigration.Migrate(current, legacy, worldSeed: 42);

        Assert.Equal(WorldCacheMigration.Outcome.Copied, outcome);
        Assert.Equal("tile", File.ReadAllText(Path.Combine(current, "tiles", "0_0.v5.sctm")));
        Assert.Equal("42", File.ReadAllText(Path.Combine(current, "world-seed.txt")));
        Assert.True(File.Exists(Path.Combine(legacy, "tiles", "0_0.v5.sctm")));
        Assert.False(Directory.EnumerateDirectories(Path.GetDirectoryName(current)!, "*.migrating*").Any());
    }

    [Fact]
    public void Old_cache_of_another_world_is_neither_copied_nor_cleared()
    {
        using var root = new TemporaryDirectory();
        var (current, legacy) = Directories(root.Path);
        WriteCache(legacy, seed: 7);

        var outcome = WorldCacheMigration.Migrate(current, legacy, worldSeed: 42);

        Assert.Equal(WorldCacheMigration.Outcome.LegacyBelongsToAnotherWorld, outcome);
        Assert.False(Directory.Exists(current));
        Assert.True(File.Exists(Path.Combine(legacy, "tiles", "0_0.v5.sctm")));
        Assert.Equal("7", File.ReadAllText(Path.Combine(legacy, "world-seed.txt")));
    }

    [Fact]
    public void Existing_new_cache_is_left_alone()
    {
        using var root = new TemporaryDirectory();
        var (current, legacy) = Directories(root.Path);
        WriteCache(legacy, seed: 42);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "world-seed.txt"), "42");

        Assert.Equal(WorldCacheMigration.Outcome.NotNeeded, WorldCacheMigration.Migrate(current, legacy, 42));
        Assert.False(Directory.Exists(Path.Combine(current, "tiles")));
    }

    [Fact]
    public void Missing_or_unstamped_old_cache_is_not_copied()
    {
        using var root = new TemporaryDirectory();
        var (current, legacy) = Directories(root.Path);

        Assert.Equal(WorldCacheMigration.Outcome.NoLegacy, WorldCacheMigration.Migrate(current, legacy, 42));
        Assert.Equal(WorldCacheMigration.Outcome.NoLegacy, WorldCacheMigration.Migrate(current, null, 42));

        Directory.CreateDirectory(Path.Combine(legacy, "tiles"));
        Assert.Equal(WorldCacheMigration.Outcome.LegacyBelongsToAnotherWorld, WorldCacheMigration.Migrate(current, legacy, 42));
        Assert.False(Directory.Exists(current));
    }

    [Fact]
    public void Seed_guard_still_resets_a_new_cache_of_another_world()
    {
        using var root = new TemporaryDirectory();
        var (current, _) = Directories(root.Path);
        WriteCache(current, seed: 7);

        Assert.Equal(WorldCacheSeedGuard.GuardOutcome.Reset, WorldCacheSeedGuard.Ensure(current, 42));
        Assert.False(Directory.Exists(Path.Combine(current, "tiles")));
    }

    private static (string Current, string Legacy) Directories(string root) => (
        Path.Combine(root, "maps", "NEWKEY", Player.ToString("N")),
        Path.Combine(root, "maps", "OLDKEY", Player.ToString("N")));

    private static void WriteCache(string directory, int seed)
    {
        Directory.CreateDirectory(Path.Combine(directory, "tiles"));
        File.WriteAllText(Path.Combine(directory, "tiles", "0_0.v5.sctm"), "tile");
        File.WriteAllText(Path.Combine(directory, "world-seed.txt"), seed.ToString());
    }
}
