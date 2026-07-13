using System.Numerics;
using System.Runtime.ExceptionServices;

namespace SurvivalcraftTravelMap.Teleport;

public sealed class SafeTeleportService
{
    private static readonly TimeSpan ChunkLoadTimeout = TimeSpan.FromSeconds(10);

    private readonly ITerrainAccess _terrain;
    private readonly IChunkLoader _chunkLoader;
    private readonly IPlayerMover _playerMover;
    private readonly IEntityCollisionQuery _collisionQuery;
    private readonly ITeleportClock _clock;
    private int _transactionActive;

    public SafeTeleportService(
        ITerrainAccess terrain,
        IChunkLoader chunkLoader,
        IPlayerMover playerMover,
        IEntityCollisionQuery collisionQuery,
        ITeleportClock clock)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(chunkLoader);
        ArgumentNullException.ThrowIfNull(playerMover);
        ArgumentNullException.ThrowIfNull(collisionQuery);
        ArgumentNullException.ThrowIfNull(clock);
        _terrain = terrain;
        _chunkLoader = chunkLoader;
        _playerMover = playerMover;
        _collisionQuery = collisionQuery;
        _clock = clock;
    }

    public async Task<TeleportResult> TeleportToSurfaceAsync(
        int x,
        int z,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _transactionActive, 1, 0) != 0)
        {
            return TeleportResult.Busy;
        }

        try
        {
            return await TeleportToSurfaceCoreAsync(x, z, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _transactionActive, 0);
        }
    }

    private async Task<TeleportResult> TeleportToSurfaceCoreAsync(
        int x,
        int z,
        CancellationToken cancellationToken)
    {
        if (!IsColumnInWorld(x, z, cancellationToken))
        {
            return TeleportResult.OutOfWorld;
        }

        var chunkLease = await LoadChunksAsync(x, z, cancellationToken).ConfigureAwait(false);
        if (chunkLease is null)
        {
            return TeleportResult.ChunkTimeout;
        }

        using (chunkLease)
        {
            foreach (var column in TeleportCandidate.GenerateSurface(x, z))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsColumnInWorld(column.X, column.Z, cancellationToken))
                {
                    continue;
                }

                var groundY = GetSurfaceHeight(column.X, column.Z, cancellationToken);
                var feetY = (long)groundY + 1L;
                if (feetY is < int.MinValue or > int.MaxValue)
                {
                    continue;
                }

                var candidate = new TeleportCandidate(column.X, (int)feetY, column.Z);
                if (IsSafe(candidate, cancellationToken))
                {
                    return await MoveTransactionallyAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
            }

            return TeleportResult.NoSafePosition;
        }
    }

    public async Task<TeleportResult> TeleportToWaypointAsync(
        Vector3 xyz,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _transactionActive, 1, 0) != 0)
        {
            return TeleportResult.Busy;
        }

        try
        {
            return await TeleportToWaypointCoreAsync(xyz, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _transactionActive, 0);
        }
    }

    private async Task<TeleportResult> TeleportToWaypointCoreAsync(
        Vector3 xyz,
        CancellationToken cancellationToken)
    {
        if (!TryFloorToInt(xyz.X, out var x)
            || !TryFloorToInt(xyz.Y, out var y)
            || !TryFloorToInt(xyz.Z, out var z)
            || !IsColumnInWorld(x, z, cancellationToken)
            || !IsCellInWorld(x, y, z, cancellationToken))
        {
            return TeleportResult.OutOfWorld;
        }

        var chunkLease = await LoadChunksAsync(x, z, cancellationToken).ConfigureAwait(false);
        if (chunkLease is null)
        {
            return TeleportResult.ChunkTimeout;
        }

        using (chunkLease)
        {
            foreach (var candidate in TeleportCandidate.GenerateWaypoint(x, y, z))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSafe(candidate, cancellationToken))
                {
                    return await MoveTransactionallyAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
            }

            return TeleportResult.NoSafePosition;
        }
    }

    private async Task<IChunkLoadLease?> LoadChunksAsync(
        int x,
        int z,
        CancellationToken cancellationToken)
    {
        using var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationSignal);

        Task<IChunkLoadLease>? loadTask = null;
        Task timeoutTask;
        try
        {
            loadTask = _chunkLoader.LoadAreaAsync(
                x,
                z,
                TeleportCandidate.SearchRadius,
                loadCancellation.Token);
            timeoutTask = _clock.DelayAsync(ChunkLoadTimeout, timeoutCancellation.Token);
        }
        catch
        {
            loadCancellation.Cancel();
            timeoutCancellation.Cancel();
            if (loadTask is not null)
            {
                DisposeAbandonedLease(loadTask);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        var completed = await Task.WhenAny(loadTask, timeoutTask, cancellationSignal.Task).ConfigureAwait(false);
        if (completed == cancellationSignal.Task)
        {
            loadCancellation.Cancel();
            timeoutCancellation.Cancel();
            DisposeAbandonedLease(loadTask);
            ObserveAbandoned(timeoutTask);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (completed == loadTask)
        {
            timeoutCancellation.Cancel();
            ObserveAbandoned(timeoutTask);
            if (cancellationToken.IsCancellationRequested)
            {
                DisposeAbandonedLease(loadTask);
                cancellationToken.ThrowIfCancellationRequested();
            }

            IChunkLoadLease lease;
            try
            {
                lease = await loadTask.ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Chunk loader returned a null lease.");
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                lease.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return lease;
        }

        loadCancellation.Cancel();
        DisposeAbandonedLease(loadTask);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await timeoutTask.ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return null;
    }

    private bool IsSafe(TeleportCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Y is not { } feetY)
        {
            return false;
        }

        var groundYLong = (long)feetY - 1L;
        var headYLong = (long)feetY + 1L;
        if (groundYLong is < int.MinValue or > int.MaxValue
            || headYLong is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        var groundY = (int)groundYLong;
        var headY = (int)headYLong;
        if (!IsColumnInWorld(candidate.X, candidate.Z, cancellationToken))
        {
            return false;
        }

        if (!IsCellInWorld(candidate.X, groundY, candidate.Z, cancellationToken)
            || !IsCellInWorld(candidate.X, feetY, candidate.Z, cancellationToken)
            || !IsCellInWorld(candidate.X, headY, candidate.Z, cancellationToken))
        {
            return false;
        }

        var groundKind = GetBlockKind(candidate.X, groundY, candidate.Z, cancellationToken);
        if (groundKind != TeleportBlockKind.SafeSolid)
        {
            return false;
        }

        var feetKind = GetBlockKind(candidate.X, feetY, candidate.Z, cancellationToken);
        if (feetKind != TeleportBlockKind.Air)
        {
            return false;
        }

        var headKind = GetBlockKind(candidate.X, headY, candidate.Z, cancellationToken);
        if (headKind != TeleportBlockKind.Air)
        {
            return false;
        }

        var position = GetPosition(candidate.X, feetY, candidate.Z);
        var hasCollision = HasBlockingCollisionExcludingPlayer(position, cancellationToken);
        return !hasCollision;
    }

    private async Task<TeleportResult> MoveTransactionallyAsync(
        TeleportCandidate candidate,
        CancellationToken cancellationToken)
    {
        var feetY = candidate.Y!.Value;
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = CaptureSnapshot(cancellationToken);
        var movement = snapshot with
        {
            Position = GetPosition(candidate.X, feetY, candidate.Z),
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
            FallDistance = 0f,
            IsFalling = false,
            HasPendingFallDamage = false,
        };

        try
        {
            _playerMover.Move(movement);
            cancellationToken.ThrowIfCancellationRequested();
            await _clock.WaitForNextUpdateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafe(candidate, cancellationToken))
            {
                throw new UnsafePostMoveValidationException();
            }

            return TeleportResult.Success;
        }
        catch (Exception originalFailure)
        {
            RestoreOrThrow(CreateSafeRollbackSnapshot(snapshot), originalFailure);
            cancellationToken.ThrowIfCancellationRequested();
            if (originalFailure is UnsafePostMoveValidationException)
            {
                return TeleportResult.RolledBack;
            }

            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw;
        }
    }

    private static Vector3 GetPosition(int x, int y, int z) =>
        new((float)((double)x + 0.5d), y, (float)((double)z + 0.5d));

    private bool IsColumnInWorld(int x, int z, CancellationToken cancellationToken)
    {
        try
        {
            var result = _terrain.IsColumnInWorld(x, z);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private bool IsCellInWorld(int x, int y, int z, CancellationToken cancellationToken)
    {
        try
        {
            var result = _terrain.IsCellInWorld(x, y, z);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private int GetSurfaceHeight(int x, int z, CancellationToken cancellationToken)
    {
        try
        {
            var result = _terrain.GetSurfaceHeight(x, z);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private TeleportBlockKind GetBlockKind(int x, int y, int z, CancellationToken cancellationToken)
    {
        try
        {
            var result = _terrain.GetBlockKind(x, y, z);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private bool HasBlockingCollisionExcludingPlayer(
        Vector3 feetPosition,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = _collisionQuery.HasBlockingCollisionExcludingPlayer(feetPosition);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private PlayerMovementSnapshot CaptureSnapshot(CancellationToken cancellationToken)
    {
        try
        {
            var result = _playerMover.CaptureSnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void RestoreOrThrow(PlayerMovementSnapshot snapshot, Exception originalFailure)
    {
        try
        {
            _playerMover.RestoreSafely(snapshot);
        }
        catch (Exception restoreFailure)
        {
            throw new TeleportRollbackException(originalFailure, restoreFailure);
        }
    }

    private static PlayerMovementSnapshot CreateSafeRollbackSnapshot(PlayerMovementSnapshot snapshot) =>
        snapshot with
        {
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
            FallDistance = 0f,
            IsFalling = false,
            HasPendingFallDamage = false,
        };

    private static bool TryFloorToInt(float value, out int result)
    {
        if (!float.IsFinite(value)
            || value < int.MinValue
            || (double)value >= (double)int.MaxValue + 1d)
        {
            result = default;
            return false;
        }

        result = (int)Math.Floor(value);
        return true;
    }

    private static void ObserveAbandoned(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void DisposeAbandonedLease(Task<IChunkLoadLease> task)
    {
        _ = task.ContinueWith(
            static completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                    return;
                }

                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    try
                    {
                        completed.Result?.Dispose();
                    }
                    catch
                    {
                        // There is no caller left to receive a late cleanup failure.
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class UnsafePostMoveValidationException : Exception;
}
