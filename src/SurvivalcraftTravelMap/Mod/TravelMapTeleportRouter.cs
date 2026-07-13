using System.Numerics;

namespace SurvivalcraftTravelMap.Mod;

public enum TravelMapTeleportDispatchResult
{
    Unavailable,
    LocalRequested,
    LocalFailed,
    CommandQueued,
}

public enum TravelMapClientTravelMode
{
    Surface,
    Waypoint,
}

public sealed record TravelMapClientTravelCommand(
    Vector3 Target,
    TravelMapClientTravelMode Mode = TravelMapClientTravelMode.Waypoint);

public sealed class TravelMapTeleportRouter(
    TravelMapWorkType workType,
    Func<Vector3, CancellationToken, Task<TravelMapTeleportDispatchResult>> localRequest,
    Action<TravelMapClientTravelCommand>? clientCommand)
{
    private readonly Func<Vector3, CancellationToken, Task<TravelMapTeleportDispatchResult>> _localRequest =
        localRequest ?? throw new ArgumentNullException(nameof(localRequest));

    public Task<TravelMapTeleportDispatchResult> RequestAsync(
        Vector3 target,
        CancellationToken cancellationToken) =>
        RequestAsync(target, TravelMapClientTravelMode.Waypoint, cancellationToken);

    public Task<TravelMapTeleportDispatchResult> RequestSurfaceAsync(
        Vector3 target,
        CancellationToken cancellationToken) =>
        RequestAsync(target, TravelMapClientTravelMode.Surface, cancellationToken);

    public Task<TravelMapTeleportDispatchResult> RequestWaypointAsync(
        Vector3 target,
        CancellationToken cancellationToken) =>
        RequestAsync(target, TravelMapClientTravelMode.Waypoint, cancellationToken);

    private Task<TravelMapTeleportDispatchResult> RequestAsync(
        Vector3 target,
        TravelMapClientTravelMode mode,
        CancellationToken cancellationToken)
    {
        if (workType == TravelMapWorkType.Local)
        {
            return _localRequest(target, cancellationToken);
        }

        if (workType == TravelMapWorkType.Client && clientCommand is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clientCommand(new TravelMapClientTravelCommand(target, mode));
            return Task.FromResult(TravelMapTeleportDispatchResult.CommandQueued);
        }

        return Task.FromResult(TravelMapTeleportDispatchResult.Unavailable);
    }
}
