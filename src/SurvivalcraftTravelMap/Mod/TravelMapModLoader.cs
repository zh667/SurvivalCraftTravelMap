using Game;
using Game.NetWork;
using SurvivalcraftTravelMap.Network;

namespace SurvivalcraftTravelMap.Mod;

public sealed class TravelMapModLoader : ModLoader
{
    public override void __ModInitialize()
    {
        if (ModsManager.GetModEntity("34GPSFix", out _))
        {
            DialogsManager.Alert(
                "Mod conflict",
                "Survivalcraft Travel Map cannot run while 34GPSFix is installed. Remove 34GPSFix and restart the game.");
            return;
        }

        TravelMapPackageRegistration.TryRegister(
            PackageManager.RegisterPackage,
            PackageManager.UnRegisterPackage,
            message => DialogsManager.Alert("Mod conflict", message));
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
