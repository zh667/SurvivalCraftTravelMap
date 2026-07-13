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
        if (!IsColumnInWorld(x, z, cancellationToken))
        {
            return TeleportResult.OutOfWorld;
        }

        if (!await LoadChunksAsync(x, z, cancellationToken).ConfigureAwait(false))
        {
            return TeleportResult.ChunkTimeout;
        }

        foreach (var column in TeleportCandidate.GenerateSurface(x, z))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsColumnInWorld(column.X, column.Z, cancellationToken))
            {
                continue;
            }

            var groundY = _terrain.GetSurfaceHeight(column.X, column.Z);
            cancellationToken.ThrowIfCancellationRequested();
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

    public async Task<TeleportResult> TeleportToWaypointAsync(
        Vector3 xyz,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryFloorToInt(xyz.X, out var x)
            || !TryFloorToInt(xyz.Y, out var y)
            || !TryFloorToInt(xyz.Z, out var z)
            || !IsColumnInWorld(x, z, cancellationToken)
            || !IsCellInWorld(x, y, z, cancellationToken))
        {
            return TeleportResult.OutOfWorld;
        }

        if (!await LoadChunksAsync(x, z, cancellationToken).ConfigureAwait(false))
        {
            return TeleportResult.ChunkTimeout;
        }

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

    private async Task<bool> LoadChunksAsync(int x, int z, CancellationToken cancellationToken)
    {
        using var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationSignal);

        Task loadTask;
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
            throw;
        }

        var completed = await Task.WhenAny(loadTask, timeoutTask, cancellationSignal.Task).ConfigureAwait(false);
        if (completed == cancellationSignal.Task)
        {
            loadCancellation.Cancel();
            timeoutCancellation.Cancel();
            ObserveAbandoned(loadTask);
            ObserveAbandoned(timeoutTask);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (completed == loadTask)
        {
            timeoutCancellation.Cancel();
            ObserveAbandoned(timeoutTask);
            await loadTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        loadCancellation.Cancel();
        ObserveAbandoned(loadTask);
        cancellationToken.ThrowIfCancellationRequested();
        await timeoutTask.ConfigureAwait(false);
        return false;
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

        var groundKind = _terrain.GetBlockKind(candidate.X, groundY, candidate.Z);
        cancellationToken.ThrowIfCancellationRequested();
        if (groundKind != TeleportBlockKind.SafeSolid)
        {
            return false;
        }

        var feetKind = _terrain.GetBlockKind(candidate.X, feetY, candidate.Z);
        cancellationToken.ThrowIfCancellationRequested();
        if (feetKind != TeleportBlockKind.Air)
        {
            return false;
        }

        var headKind = _terrain.GetBlockKind(candidate.X, headY, candidate.Z);
        cancellationToken.ThrowIfCancellationRequested();
        if (headKind != TeleportBlockKind.Air)
        {
            return false;
        }

        var position = GetPosition(candidate.X, feetY, candidate.Z);
        var hasCollision = _collisionQuery.HasCollision(position);
        cancellationToken.ThrowIfCancellationRequested();
        return !hasCollision;
    }

    private async Task<TeleportResult> MoveTransactionallyAsync(
        TeleportCandidate candidate,
        CancellationToken cancellationToken)
    {
        var feetY = candidate.Y!.Value;
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _playerMover.CaptureSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
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
                var postValidationFailure = new InvalidOperationException(
                    "The teleport destination became unsafe after movement.");
                try
                {
                    _playerMover.Restore(snapshot);
                }
                catch (Exception restoreFailure)
                {
                    throw new TeleportRollbackException(postValidationFailure, restoreFailure);
                }

                return TeleportResult.RolledBack;
            }

            return TeleportResult.Success;
        }
        catch (TeleportRollbackException)
        {
            throw;
        }
        catch (Exception originalFailure)
        {
            try
            {
                _playerMover.Restore(snapshot);
            }
            catch (Exception restoreFailure)
            {
                throw new TeleportRollbackException(originalFailure, restoreFailure);
            }

            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw;
        }
    }

    private static Vector3 GetPosition(int x, int y, int z) =>
        new((float)((double)x + 0.5d), y, (float)((double)z + 0.5d));

    private bool IsColumnInWorld(int x, int z, CancellationToken cancellationToken)
    {
        var result = _terrain.IsColumnInWorld(x, z);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private bool IsCellInWorld(int x, int y, int z, CancellationToken cancellationToken)
    {
        var result = _terrain.IsCellInWorld(x, y, z);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

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
}
