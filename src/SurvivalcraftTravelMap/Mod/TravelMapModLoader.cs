using Game;
using Game.NetWork;
using SurvivalcraftTravelMap.Network;
using System.Xml.Linq;

namespace SurvivalcraftTravelMap.Mod;

public sealed class TravelMapModLoader : ModLoader
{
    public override void OnXdbLoad(XElement database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var injected = false;
        Entity.GetFiles(".netxdb", (_, stream) =>
        {
            try
            {
                var source = XElement.Load(stream, LoadOptions.None);
                TravelMapDatabaseInjector.Inject(source, database);
                injected = true;
                Engine.Log.Information("[TravelMap] Database component template injected.");
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
            {
                Engine.Log.Warning($"[TravelMap] Database injection failed: {exception.Message}");
            }
        });

        if (!injected)
        {
            Engine.Log.Warning("[TravelMap] mod.netxdb was not found or could not be injected; the player component is unavailable.");
        }
    }

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

internal static class TravelMapDatabaseInjector
{
    private static readonly string[] AnchorElementNames = ["EntityTemplate", "Folder"];

    internal static void Inject(XElement source, XElement database)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(database);
        if (source.Name.LocalName != "SurvivalCraftMap")
        {
            throw new InvalidDataException("The travel-map database root must be SurvivalCraftMap.");
        }

        var additions = new List<(XElement Target, XElement Child)>();
        var plannedGuids = new HashSet<Guid>();
        foreach (var anchorName in AnchorElementNames)
        {
            foreach (var sourceAnchor in source.Descendants(anchorName))
            {
                var anchorGuid = GetRequiredGuid(sourceAnchor);
                var targetAnchor = FindByGuid(database, anchorGuid)
                    ?? throw new InvalidDataException(
                        $"The base database anchor {anchorName} ({anchorGuid}) was not found.");
                foreach (var sourceChild in sourceAnchor.Elements())
                {
                    var childGuid = GetRequiredGuid(sourceChild);
                    var existing = FindByGuid(database, childGuid);
                    if (existing is not null)
                    {
                        if (!ReferenceEquals(existing.Parent, targetAnchor)
                            || !XNode.DeepEquals(existing, sourceChild))
                        {
                            throw new InvalidDataException(
                                $"Database object GUID {childGuid} is already used by another definition.");
                        }

                        continue;
                    }

                    if (!plannedGuids.Add(childGuid))
                    {
                        throw new InvalidDataException(
                            $"Database object GUID {childGuid} occurs more than once in mod.netxdb.");
                    }

                    additions.Add((targetAnchor, new XElement(sourceChild)));
                }
            }
        }

        foreach (var (target, child) in additions)
        {
            target.Add(child);
        }
    }

    private static Guid GetRequiredGuid(XElement element)
    {
        var text = element.Attribute("Guid")?.Value;
        return Guid.TryParse(text, out var guid)
            ? guid
            : throw new InvalidDataException(
                $"Database element {element.Name.LocalName} has no valid Guid attribute.");
    }

    private static XElement? FindByGuid(XElement root, Guid guid) =>
        root.DescendantsAndSelf().FirstOrDefault(element => HasGuid(element, guid));

    private static bool HasGuid(XElement element, Guid expected) =>
        Guid.TryParse(element.Attribute("Guid")?.Value, out var actual) && actual == expected;
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
