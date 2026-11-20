using System.Globalization;

namespace SurvivalcraftTravelMap.Persistence;

/// <summary>
/// Local worlds used to share a cache directory with every same-named world of another install
/// (the key was the game's world directory name). Now that they are keyed by the full path, the
/// old cache is copied to the new place once -- only when it carries this world's seed, so a map
/// that belongs to another install's world is neither taken over nor cleared. The old directory is
/// kept, so an older build still finds its map after a downgrade.
/// </summary>
public static class WorldCacheMigration
{
    public enum Outcome
    {
        /// <summary>The new cache already exists; nothing was copied.</summary>
        NotNeeded,

        /// <summary>There is no old cache to copy.</summary>
        NoLegacy,

        /// <summary>The old cache carries no seed or another world's seed; left untouched.</summary>
        LegacyBelongsToAnotherWorld,

        /// <summary>The old cache was copied to the new directory.</summary>
        Copied,
    }

    public static Outcome Migrate(string directory, string? legacyDirectory, int worldSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (Directory.Exists(directory))
        {
            return Outcome.NotNeeded;
        }

        if (string.IsNullOrWhiteSpace(legacyDirectory) || !Directory.Exists(legacyDirectory))
        {
            return Outcome.NoLegacy;
        }

        var stamp = WorldCacheSeedGuard.ReadStamp(legacyDirectory);
        if (!string.Equals(stamp, worldSeed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return Outcome.LegacyBelongsToAnotherWorld;
        }

        // Copy next to the target and rename, so an interrupted copy never looks like a cache.
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory))
            ?? throw new ArgumentException("The cache directory has no parent.", nameof(directory));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, Path.GetFileName(directory) + ".migrating-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(legacyDirectory, staging);
            Directory.Move(staging, directory);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        return Outcome.Copied;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }
}
