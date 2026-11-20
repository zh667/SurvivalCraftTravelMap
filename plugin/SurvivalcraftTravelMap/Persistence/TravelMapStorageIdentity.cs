using SurvivalcraftTravelMap.Mod;

namespace SurvivalcraftTravelMap.Persistence;

public enum TravelMapStorageScope
{
    LocalWorld,
    RemoteServer,
}

public sealed record TravelMapStorageIdentityInput(
    TravelMapStorageScope Scope,
    string ApplicationRoot,
    string? LocalWorldDirectory,
    string? ServerHost,
    int? ServerPort,
    string? ServerWorldIdentifier,
    Guid PlayerGuid,
    string? LocalWorldSystemPath = null);

/// <param name="LegacyDirectory">
/// For a local world keyed by its system path: where versions up to 2.9.0's first build kept its
/// cache (keyed by the game's world directory name only), to copy from once.
/// </param>
public sealed record TravelMapStorageLocation(
    string Directory,
    string WorldKey,
    Guid PlayerGuid,
    string? LegacyDirectory = null);

public static class TravelMapStorageIdentity
{
    public static bool TryResolve(
        TravelMapStorageIdentityInput input,
        out TravelMapStorageLocation? location,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(input);
        location = null;
        if (input.PlayerGuid == Guid.Empty)
        {
            failureReason = "The local player identity is unavailable.";
            return false;
        }

        string worldKey;
        string? legacyKey = null;
        try
        {
            if (input.Scope == TravelMapStorageScope.LocalWorld)
            {
                if (string.IsNullOrWhiteSpace(input.LocalWorldDirectory))
                {
                    failureReason = "The local world directory identity is unavailable.";
                    return false;
                }

                worldKey = WorldKey.ForLocal(input.LocalWorldDirectory);
                if (!string.IsNullOrWhiteSpace(input.LocalWorldSystemPath))
                {
                    try
                    {
                        legacyKey = worldKey;
                        worldKey = WorldKey.ForLocalPath(input.LocalWorldSystemPath);
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        // An unusable system path: keep the directory-name key as before.
                        worldKey = legacyKey;
                        legacyKey = null;
                    }
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(input.ServerHost)
                    || !input.ServerPort.HasValue
                    || string.IsNullOrWhiteSpace(input.ServerWorldIdentifier))
                {
                    failureReason = "The server endpoint or server world identity is unavailable.";
                    return false;
                }

                worldKey = WorldKey.ForServer(
                    input.ServerHost,
                    input.ServerPort.Value,
                    input.ServerWorldIdentifier);
            }
        }
        catch (ArgumentException exception)
        {
            failureReason = exception.Message;
            return false;
        }

        string PlayerDirectory(string key) => Path.GetFullPath(Path.Combine(
            input.ApplicationRoot,
            "maps",
            key,
            input.PlayerGuid.ToString("N")));
        var directory = PlayerDirectory(worldKey);
        location = new TravelMapStorageLocation(
            directory,
            worldKey,
            input.PlayerGuid,
            legacyKey is null ? null : PlayerDirectory(legacyKey));
        failureReason = string.Empty;
        return true;
    }
}
