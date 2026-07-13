namespace SurvivalcraftTravelMap.Teleport;

using Game;

public sealed class GameUpdateDispatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<IQueuedWork> _work = [];
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

        var invocation = new QueuedInvocation<T>(action);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _work.Enqueue(invocation);
        }

        return invocation.Task.GetAwaiter().GetResult();
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

        IQueuedWork[] work;
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

        foreach (var item in work)
        {
            item.Execute();
        }
    }

    public void Dispose()
    {
        IQueuedWork[] work;
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

        foreach (var item in work)
        {
            item.Fail(exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct NextUpdateWaiter(
        TaskCompletionSource Completion,
        CancellationTokenRegistration Registration);

    private interface IQueuedWork
    {
        void Execute();

        void Fail(Exception exception);
    }

    private sealed class QueuedInvocation<T>(Func<T> action) : IQueuedWork
    {
        private readonly Func<T> _action = action;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<T> Task => _completion.Task;

        public void Execute()
        {
            try
            {
                _completion.TrySetResult(_action());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
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

    void ReleaseArea();
}

public abstract class DispatcherBoundChunkFacade(GameUpdateDispatcher dispatcher) : ISurvivalcraftChunkFacade
{
    private readonly GameUpdateDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public void RequestArea(int centerX, int centerZ, int radius) =>
        _dispatcher.Invoke(() => RequestAreaOnUpdateThread(centerX, centerZ, radius));

    public bool IsAreaReady(int centerX, int centerZ, int radius) =>
        _dispatcher.Invoke(() => IsAreaReadyOnUpdateThread(centerX, centerZ, radius));

    public void ReleaseArea() => _dispatcher.Invoke(ReleaseAreaOnUpdateThread);

    protected abstract void RequestAreaOnUpdateThread(int centerX, int centerZ, int radius);

    protected abstract bool IsAreaReadyOnUpdateThread(int centerX, int centerZ, int radius);

    protected abstract void ReleaseAreaOnUpdateThread();
}

public sealed class SurvivalcraftChunkLoader : IChunkLoader, IDisposable
{
    private readonly ISurvivalcraftChunkFacade _facade;
    private readonly ITeleportClock _clock;
    private readonly IDisposable? _ownedFacade;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _leaseSync = new();
    private ChunkLoadLease? _activeLease;
    private int _disposed;

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

    public async Task<IChunkLoadLease> LoadAreaAsync(
        int centerX,
        int centerZ,
        int radius,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var gateHeld = false;
        var requested = false;
        try
        {
            await _requestGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _facade.RequestArea(centerX, centerZ, radius);
            requested = true;
            while (true)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (_facade.IsAreaReady(centerX, centerZ, radius))
                {
                    var lease = new ChunkLoadLease(this);
                    lock (_leaseSync)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                        _activeLease = lease;
                    }

                    gateHeld = false;
                    requested = false;
                    return lease;
                }

                await _clock.WaitForNextUpdateAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (requested)
            {
                try
                {
                    _facade.ReleaseArea();
                }
                finally
                {
                    if (gateHeld)
                    {
                        _requestGate.Release();
                    }
                }
            }
            else if (gateHeld)
            {
                _requestGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        ChunkLoadLease? lease;
        lock (_leaseSync)
        {
            lease = _activeLease;
        }

        try
        {
            lease?.Dispose();
        }
        finally
        {
            _ownedFacade?.Dispose();
        }
    }

    private void Release(ChunkLoadLease lease)
    {
        lock (_leaseSync)
        {
            if (!ReferenceEquals(_activeLease, lease))
            {
                return;
            }

            _activeLease = null;
        }

        try
        {
            _facade.ReleaseArea();
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private sealed class ChunkLoadLease(SurvivalcraftChunkLoader owner) : IChunkLoadLease
    {
        private SurvivalcraftChunkLoader? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(this);
    }
}

internal sealed class SurvivalcraftChunkFacade : DispatcherBoundChunkFacade, IDisposable
{
    private const int ChunkSize = 16;

    private readonly SubsystemTerrain _terrain;
    private readonly int _updateLocationId;
    private readonly GameUpdateDispatcher _dispatcher;
    private bool _requested;
    private int _disposed;

    internal SurvivalcraftChunkFacade(
        SubsystemTerrain terrain,
        int updateLocationId,
        GameUpdateDispatcher dispatcher)
        : base(dispatcher)
    {
        _terrain = terrain;
        _updateLocationId = updateLocationId;
        _dispatcher = dispatcher;
    }

    protected override void RequestAreaOnUpdateThread(int centerX, int centerZ, int radius)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var distance = Math.Max(32f, radius + ChunkSize * 2f);
        _terrain.TerrainUpdater.SetUpdateLocation(
            _updateLocationId,
            new Engine.Vector2(centerX + 0.5f, centerZ + 0.5f),
            distance,
            distance);
        _requested = true;
    }

    protected override bool IsAreaReadyOnUpdateThread(int centerX, int centerZ, int radius)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return AreChunksReadyOnUpdateThread(centerX, centerZ, radius);
    }

    protected override void ReleaseAreaOnUpdateThread()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ReleaseAreaCore();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.Invoke(ReleaseAreaCore);
    }

    private void ReleaseAreaCore()
    {
        if (_requested)
        {
            _terrain.TerrainUpdater.RemoveUpdateLocation(_updateLocationId);
            _requested = false;
        }
    }

    private bool AreChunksReadyOnUpdateThread(int centerX, int centerZ, int radius)
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
