namespace SurvivalcraftTravelMap.Mod;

public enum TravelMapLoadStage
{
    Dispatcher,
    Settings,
    Waypoints,
    Resources,
    Widgets,
}

public sealed class IdempotentTravelMapCleanup(Action cleanup)
{
    private Action? _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));

    public void Run() => Interlocked.Exchange(ref _cleanup, null)?.Invoke();
}

public static class TravelMapLoadTransaction
{
    public static bool TryRun(
        IReadOnlyList<Action> stages,
        Action cleanup,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(reportFailure);
        try
        {
            foreach (var stage in stages)
            {
                ArgumentNullException.ThrowIfNull(stage);
                stage();
            }

            return true;
        }
        catch (Exception exception)
        {
            try
            {
                cleanup();
            }
            catch (Exception cleanupException)
            {
                SafeReport(reportFailure, new AggregateException(
                    "Travel-map activation and cleanup both failed.",
                    exception,
                    cleanupException));
                return false;
            }

            SafeReport(reportFailure, exception);
            return false;
        }
    }

    private static void SafeReport(Action<Exception> reportFailure, Exception exception)
    {
        try
        {
            reportFailure(exception);
        }
        catch
        {
        }
    }
}
