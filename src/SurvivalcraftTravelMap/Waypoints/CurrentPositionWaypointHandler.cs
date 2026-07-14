using System.Globalization;
using System.Numerics;

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
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"当前位置 {position.X:0.##}, {position.Y:0.##}, {position.Z:0.##}");
        _repository.Add(name, position);
        return await WaypointPersistence.SaveOrReloadAsync(
            _repository,
            cancellationToken).ConfigureAwait(false);
    }
}
