using Game;

namespace SurvivalcraftTravelMap.Mod;

public sealed class TravelMapModLoader : ModLoader
{
    public override void __ModInitialize()
    {
        // The Player ComponentTemplate is registered through Assets/TravelMap.xdb, which the
        // API loads automatically. Here we only guard against the legacy multiplayer GPS mod.
        TravelMapStartup.EnsureInitialized(
            packageName => ModsManager.GetModEntity(packageName, out _),
            message => Engine.Log.Warning($"[TravelMap] {message}"));
    }
}

public enum TravelMapStartupState
{
    Uninitialized,
    Initializing,
    Active,
    LegacyConflict,
}

public static class TravelMapStartup
{
    public const string LegacyPackageName = "34GPSFix";
    private static readonly object Sync = new();
    private static TravelMapStartupState s_state;

    public static TravelMapStartupState CurrentState
    {
        get
        {
            lock (Sync)
            {
                return s_state;
            }
        }
    }

    public static bool IsActive => CurrentState == TravelMapStartupState.Active;

    public static bool HasLegacyConflict(Func<string, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        return isInstalled(LegacyPackageName);
    }

    public static bool EnsureInitialized(
        Func<string, bool> isInstalled,
        Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        ArgumentNullException.ThrowIfNull(reportError);
        lock (Sync)
        {
            if (s_state != TravelMapStartupState.Uninitialized)
            {
                return s_state == TravelMapStartupState.Active;
            }

            s_state = TravelMapStartupState.Initializing;
            if (HasLegacyConflict(isInstalled))
            {
                s_state = TravelMapStartupState.LegacyConflict;
                reportError(
                    "Survivalcraft Travel Map cannot run while 34GPSFix is installed. "
                    + "Remove 34GPSFix and restart the game.");
                return false;
            }

            s_state = TravelMapStartupState.Active;
            return true;
        }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            s_state = TravelMapStartupState.Uninitialized;
        }
    }
}
