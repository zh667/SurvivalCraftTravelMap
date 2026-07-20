using System.Numerics;
using SurvivalcraftTravelMap.UI;

namespace SurvivalcraftTravelMap.Waypoints;

public sealed class CurrentPositionWaypointHandler
{
    private readonly WaypointRepository _repository;
    private readonly Func<Vector3> _currentPosition;

    public CurrentPositionWaypointHandler(
        WaypointRepository repository,
        Func<Vector3> currentPosition)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _currentPosition = currentPosition ?? throw new ArgumentNullException(nameof(currentPosition));
    }

    public async Task<IReadOnlyList<Waypoint>> SaveAsync(
        CancellationToken cancellationToken = default)
    {
        var position = _currentPosition();
        var name = TravelMapText.Format(
            "currentPositionWaypointFormat",
            "当前位置 {0:0.##}, {1:0.##}, {2:0.##}",
            position.X,
            position.Y,
            position.Z);
        _repository.Add(name, position);
        return await WaypointPersistence.SaveOrReloadAsync(
            _repository,
            cancellationToken).ConfigureAwait(false);
    }
}
