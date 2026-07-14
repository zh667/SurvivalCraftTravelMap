namespace SurvivalcraftTravelMap.UI;

public sealed class TrackedUiActionRunner : IDisposable
{
    private readonly object _sync = new();
    private readonly Action<Exception> _reportFailure;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _idle = CompletedSource();
    private int _active;
    private bool _disposed;
    private bool _cancellationCompleted;
    private bool _lifetimeDisposed;

    public TrackedUiActionRunner(Action<Exception> reportFailure)
    {
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public bool TryRun(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (_disposed || _active != 0)
            {
                return false;
            }

            _active = 1;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RunObservedAsync(action, _idle);
            return true;
        }
    }

    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_sync)
        {
            idle = _idle.Task;
        }

        return idle.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        var disposeLifetime = false;
        try
        {
            _lifetime.Cancel();
        }
        finally
        {
            lock (_sync)
            {
                _cancellationCompleted = true;
                disposeLifetime = TakeLifetimeDisposalLocked();
            }

            if (disposeLifetime)
            {
                _lifetime.Dispose();
            }
        }
    }

    private async Task RunObservedAsync(
        Func<CancellationToken, Task> action,
        TaskCompletionSource idle)
    {
        try
        {
            await action(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _reportFailure(exception);
        }
        finally
        {
            var disposeLifetime = false;
            lock (_sync)
            {
                _active = 0;
                idle.TrySetResult();
                disposeLifetime = TakeLifetimeDisposalLocked();
            }

            if (disposeLifetime)
            {
                _lifetime.Dispose();
            }
        }
    }

    private bool TakeLifetimeDisposalLocked()
    {
        if (!_disposed || !_cancellationCompleted || _active != 0 || _lifetimeDisposed)
        {
            return false;
        }

        _lifetimeDisposed = true;
        return true;
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
