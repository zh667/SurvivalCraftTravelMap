namespace SurvivalcraftTravelMap.UI;

public sealed class CoalescingSaveQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task> _save;
    private readonly Action<Exception> _reportFailure;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _idle = CompletedSource();
    private Task? _worker;
    private long _requestedVersion;
    private bool _disposed;

    public CoalescingSaveQueue(
        Func<CancellationToken, Task> save,
        Action<Exception> reportFailure,
        TimeSpan debounce)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        if (debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        _debounce = debounce;
    }

    public void RequestSave()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _requestedVersion++;
            if (_worker is null)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _worker = RunAsync();
            }
        }
    }

    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return _idle.Task.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        var disposeLifetime = false;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            disposeLifetime = _worker is null;
        }

        _lifetime.Cancel();
        if (disposeLifetime)
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        var completionPublished = false;
        var disposeLifetime = false;
        try
        {
            while (true)
            {
                long targetVersion;
                lock (_sync)
                {
                    targetVersion = _requestedVersion;
                }

                await Task.Delay(_debounce, _lifetime.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (targetVersion != _requestedVersion)
                    {
                        continue;
                    }
                }

                try
                {
                    await _save(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SafeReport(exception);
                }

                lock (_sync)
                {
                    if (targetVersion == _requestedVersion)
                    {
                        _worker = null;
                        _idle.TrySetResult();
                        completionPublished = true;
                        disposeLifetime = _disposed;
                    }
                }

                if (completionPublished)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!completionPublished)
            {
                lock (_sync)
                {
                    _worker = null;
                    _idle.TrySetResult();
                    disposeLifetime = _disposed;
                }
            }

            if (disposeLifetime)
            {
                _lifetime.Dispose();
            }
        }
    }

    private void SafeReport(Exception exception)
    {
        try
        {
            _reportFailure(exception);
        }
        catch
        {
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
