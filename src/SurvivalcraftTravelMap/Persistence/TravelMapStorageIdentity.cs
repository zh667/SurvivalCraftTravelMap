using SurvivalcraftTravelMap.Mod;

namespace SurvivalcraftTravelMap.Persistence;

public sealed record TravelMapStorageIdentityInput(
    TravelMapWorkType WorkType,
    string ApplicationRoot,
    string? LocalWorldDirectory,
    string? ServerHost,
    int? ServerPort,
    string? ServerWorldIdentifier,
    Guid PlayerGuid);

public sealed record TravelMapStorageLocation(string Directory, string WorldKey, Guid PlayerGuid);

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
        try
        {
            if (input.WorkType == TravelMapWorkType.Local)
            {
                if (string.IsNullOrWhiteSpace(input.LocalWorldDirectory))
                {
                    failureReason = "The local world directory identity is unavailable.";
                    return false;
                }

                worldKey = WorldKey.ForLocal(input.LocalWorldDirectory);
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

        var directory = Path.GetFullPath(Path.Combine(
            input.ApplicationRoot,
            "maps",
            worldKey,
            input.PlayerGuid.ToString("N")));
        location = new TravelMapStorageLocation(directory, worldKey, input.PlayerGuid);
        failureReason = string.Empty;
        return true;
    }
}
