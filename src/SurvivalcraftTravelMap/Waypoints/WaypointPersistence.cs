namespace SurvivalcraftTravelMap.Waypoints;

public static class WaypointPersistence
{
    public static async Task<IReadOnlyList<Waypoint>> SaveOrReloadAsync(
        WaypointRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        try
        {
            await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
            return repository.GetAll();
        }
        catch (Exception saveException)
        {
            try
            {
                await repository.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reloadException)
            {
                throw new AggregateException(
                    "Waypoint save failed and the persisted state could not be reloaded.",
                    saveException,
                    reloadException);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(saveException).Throw();
            throw;
        }
    }
}
