using System.Globalization;

namespace SurvivalcraftTravelMap.Persistence;

/// <summary>
/// Survivalcraft recycles world folder names (<c>World</c>, <c>World1</c>, …), handing a freshly
/// created world the name of a previously deleted one. Because the travel-map cache is keyed by that
/// folder name, a recycled name would otherwise surface the deleted world's explored terrain. This
/// guard stamps each cache directory with its world's seed and wipes the directory when the stored
/// seed no longer matches — i.e. when the folder name has been reused by a different world.
/// </summary>
public static class WorldCacheSeedGuard
{
    private const string StampFileName = "world-seed.txt";

    public enum GuardOutcome
    {
        /// <summary>The stamp matched (or was freshly written); any existing cache was kept.</summary>
        Matched,

        /// <summary>The folder name was reused by a different world; the stale cache was discarded.</summary>
        Reset,
    }

    /// <summary>
    /// Ensures <paramref name="storageDirectory"/> belongs to the world identified by
    /// <paramref name="worldSeed"/>. When a mismatching stamp is found the directory is deleted and
    /// recreated. A fresh, correct stamp is always written on return.
    /// </summary>
    public static GuardOutcome Ensure(string storageDirectory, int worldSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);

        var stamp = worldSeed.ToString(CultureInfo.InvariantCulture);
        var stampPath = Path.Combine(storageDirectory, StampFileName);
        var outcome = GuardOutcome.Matched;

        if (File.Exists(stampPath)
            && !string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal))
        {
            // Same folder name, different world: the game reused a deleted world's directory.
            Directory.Delete(storageDirectory, recursive: true);
            outcome = GuardOutcome.Reset;
        }

        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(stampPath, stamp);
        return outcome;
    }
}
