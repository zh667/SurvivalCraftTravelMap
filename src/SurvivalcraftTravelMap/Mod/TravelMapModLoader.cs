using Game;
using Game.NetWork;
using SurvivalcraftTravelMap.Network;

namespace SurvivalcraftTravelMap.Mod;

public sealed class TravelMapModLoader : ModLoader
{
    public override void __ModInitialize()
    {
        TravelMapStartup.EnsureInitialized(
            packageName => ModsManager.GetModEntity(packageName, out _),
            PackageManager.RegisterPackage,
            PackageManager.UnRegisterPackage,
            message =>
            {
                Engine.Log.Warning($"[TravelMap] {message}");
                DialogsManager.Alert("Mod conflict", message);
            });
    }
}

public enum TravelMapStartupState
{
    Uninitialized,
    Initializing,
    Active,
    LegacyConflict,
    RegistrationFailed,
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

    public static bool TryInitialize(
        Func<string, bool> isInstalled,
        Action<IPackage> register,
        Action<IPackage> unregister,
        Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(reportError);
        if (HasLegacyConflict(isInstalled))
        {
            reportError(
                "Survivalcraft Travel Map cannot run while 34GPSFix is installed. "
                + "Remove 34GPSFix and restart the game.");
            return false;
        }

        return TravelMapPackageRegistration.TryRegister(register, unregister, reportError);
    }

    public static bool EnsureInitialized(
        Func<string, bool> isInstalled,
        Action<IPackage> register,
        Action<IPackage> unregister,
        Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(unregister);
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

            var registered = TravelMapPackageRegistration.TryRegister(
                register,
                unregister,
                reportError);
            s_state = registered
                ? TravelMapStartupState.Active
                : TravelMapStartupState.RegistrationFailed;
            return registered;
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

public static class TravelMapPackageRegistration
{
    public static bool TryRegister(
        Action<IPackage> register,
        Action<IPackage> unregister,
        Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(unregister);
        ArgumentNullException.ThrowIfNull(reportError);
        IPackage[] packages = [new LegacyGpsPackage(), new CoordinateTeleportPackage()];
        var registered = new List<IPackage>(packages.Length);
        foreach (var package in packages)
        {
            try
            {
                register(package);
                registered.Add(package);
            }
            catch (Exception exception)
            {
                foreach (var completed in registered.AsEnumerable().Reverse())
                {
                    try
                    {
                        unregister(completed);
                    }
                    catch
                    {
                    }
                }

                reportError(
                    $"Survivalcraft Travel Map could not register network package ID {package.ID}: {exception.Message}");
                return false;
            }
        }

        return true;
    }
}
