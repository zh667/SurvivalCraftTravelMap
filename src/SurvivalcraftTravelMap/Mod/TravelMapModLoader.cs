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

        PackageManager.RegisterPackage(new LegacyGpsPackage());
        PackageManager.RegisterPackage(new CoordinateTeleportPackage());
    }
}
