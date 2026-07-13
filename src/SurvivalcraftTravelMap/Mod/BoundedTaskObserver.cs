namespace SurvivalcraftTravelMap.Mod;

public static class BoundedTaskObserver
{
    public static bool ObserveWithin(
        Task task,
        TimeSpan timeout,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var completed = task.IsCompleted
            || ReferenceEquals(
                Task.WhenAny(task, Task.Delay(timeout)).GetAwaiter().GetResult(),
                task);
        if (completed)
        {
            ObserveCompletion(task, reportFailure);
            return true;
        }

        _ = task.ContinueWith(
            completedTask => SafeReport(
                reportFailure,
                completedTask.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return false;
    }

    private static void ObserveCompletion(Task task, Action<Exception> reportFailure)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SafeReport(reportFailure, exception);
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
