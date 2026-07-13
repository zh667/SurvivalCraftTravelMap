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
    TravelMapRuntimeContext runtimeContext,
    Func<Vector3, CancellationToken, Task<TravelMapTeleportDispatchResult>> localRequest,
    Func<TravelMapClientTravelCommand, CancellationToken, Task<TravelMapTeleportDispatchResult>>? authoritativeHostRequest,
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
        if (runtimeContext.WorkType == TravelMapWorkType.Local)
        {
            return _localRequest(target, cancellationToken);
        }

        var command = new TravelMapClientTravelCommand(target, mode);
        if (TravelMapRuntimePolicy.UsesAuthoritativeHostTeleport(runtimeContext)
            && authoritativeHostRequest is not null)
        {
            return authoritativeHostRequest(command, cancellationToken);
        }

        if (runtimeContext.WorkType == TravelMapWorkType.Client && clientCommand is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clientCommand(command);
            return Task.FromResult(TravelMapTeleportDispatchResult.CommandQueued);
        }

        return Task.FromResult(TravelMapTeleportDispatchResult.Unavailable);
    }
}
