namespace SurvivalcraftTravelMap.Teleport;

using Game;

public sealed class GameUpdateDispatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Action> _work = [];
    private readonly List<NextUpdateWaiter> _nextUpdateWaiters = [];
    private readonly int _updateThreadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _work.Count;
            }
        }
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Invoke(() =>
        {
            action();
            return true;
        });
    }

    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();
        if (Environment.CurrentManagedThreadId == _updateThreadId)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _work.Enqueue(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    public Task WaitForNextUpdateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource();
        var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            completion);
        lock (_sync)
        {
            if (_disposed)
            {
                registration.Dispose();
                throw new ObjectDisposedException(nameof(GameUpdateDispatcher));
            }

            _nextUpdateWaiters.Add(new NextUpdateWaiter(completion, registration));
        }

        return completion.Task;
    }

    public void Pump()
    {
        ThrowIfDisposed();
        if (Environment.CurrentManagedThreadId != _updateThreadId)
        {
            throw new InvalidOperationException("Game update work must be pumped by the owning update thread.");
        }

        Action[] work;
        NextUpdateWaiter[] waiters;
        lock (_sync)
        {
            work = _work.ToArray();
            _work.Clear();
            waiters = _nextUpdateWaiters.ToArray();
            _nextUpdateWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetResult();
        }

        foreach (var action in work)
        {
            action();
        }
    }

    public void Dispose()
    {
        Action[] work;
        NextUpdateWaiter[] waiters;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            work = _work.ToArray();
            _work.Clear();
            waiters = _nextUpdateWaiters.ToArray();
            _nextUpdateWaiters.Clear();
        }

        var exception = new ObjectDisposedException(nameof(GameUpdateDispatcher));
        foreach (var waiter in waiters)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetException(exception);
        }

        foreach (var action in work)
        {
            action();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct NextUpdateWaiter(
        TaskCompletionSource Completion,
        CancellationTokenRegistration Registration);
}

public sealed class SurvivalcraftTeleportClock(GameUpdateDispatcher dispatcher) : ITeleportClock
{
    private readonly GameUpdateDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);

    public Task WaitForNextUpdateAsync(CancellationToken cancellationToken) =>
        _dispatcher.WaitForNextUpdateAsync(cancellationToken);
}

public interface ISurvivalcraftChunkFacade
{
    void RequestArea(int centerX, int centerZ, int radius);

    bool IsAreaReady(int centerX, int centerZ, int radius);
}

public sealed class SurvivalcraftChunkLoader : IChunkLoader, IDisposable
{
    private readonly ISurvivalcraftChunkFacade _facade;
    private readonly ITeleportClock _clock;
    private readonly IDisposable? _ownedFacade;

    public SurvivalcraftChunkLoader(ISurvivalcraftChunkFacade facade, ITeleportClock clock)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(clock);
        _facade = facade;
        _clock = clock;
    }

    public SurvivalcraftChunkLoader(
        SubsystemTerrain terrain,
        int updateLocationId,
        GameUpdateDispatcher dispatcher,
        ITeleportClock clock)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        var facade = new SurvivalcraftChunkFacade(terrain, updateLocationId, dispatcher);
        _facade = facade;
        _clock = clock;
        _ownedFacade = facade;
    }

    public async Task LoadAreaAsync(
        int centerX,
        int centerZ,
        int radius,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _facade.RequestArea(centerX, centerZ, radius);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_facade.IsAreaReady(centerX, centerZ, radius))
            {
                return;
            }

            await _clock.WaitForNextUpdateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _ownedFacade?.Dispose();
}

internal sealed class SurvivalcraftChunkFacade : ISurvivalcraftChunkFacade, IDisposable
{
    private const int ChunkSize = 16;

    private readonly SubsystemTerrain _terrain;
    private readonly int _updateLocationId;
    private readonly GameUpdateDispatcher _dispatcher;
    private bool _requested;
    private bool _disposed;

    internal SurvivalcraftChunkFacade(
        SubsystemTerrain terrain,
        int updateLocationId,
        GameUpdateDispatcher dispatcher)
    {
        _terrain = terrain;
        _updateLocationId = updateLocationId;
        _dispatcher = dispatcher;
    }

    public void RequestArea(int centerX, int centerZ, int radius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.Invoke(() =>
        {
            var distance = Math.Max(32f, radius + ChunkSize * 2f);
            _terrain.TerrainUpdater.SetUpdateLocation(
                _updateLocationId,
                new Engine.Vector2(centerX + 0.5f, centerZ + 0.5f),
                distance,
                distance);
            _requested = true;
        });
    }

    public bool IsAreaReady(int centerX, int centerZ, int radius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.Invoke(() => IsAreaReadyOnUpdateThread(centerX, centerZ, radius));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_requested)
        {
            _dispatcher.Invoke(() => _terrain.TerrainUpdater.RemoveUpdateLocation(_updateLocationId));
        }
    }

    private bool IsAreaReadyOnUpdateThread(int centerX, int centerZ, int radius)
    {
        var minX = ClampToInt((long)centerX - radius);
        var minZ = ClampToInt((long)centerZ - radius);
        var maxX = ClampToInt((long)centerX + radius);
        var maxZ = ClampToInt((long)centerZ + radius);
        var minChunk = Terrain.ToChunk(minX, minZ);
        var maxChunk = Terrain.ToChunk(maxX, maxZ);
        for (var chunkX = minChunk.X; chunkX <= maxChunk.X; chunkX++)
        {
            for (var chunkZ = minChunk.Y; chunkZ <= maxChunk.Y; chunkZ++)
            {
                var chunk = _terrain.Terrain.GetChunkAtCoords(chunkX, chunkZ);
                if (chunk is null || chunk.State != TerrainChunkState.Valid)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int ClampToInt(long value) => value switch
    {
        < int.MinValue => int.MinValue,
        > int.MaxValue => int.MaxValue,
        _ => (int)value,
    };
}
