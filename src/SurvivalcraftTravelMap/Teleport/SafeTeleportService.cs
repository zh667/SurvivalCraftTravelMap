using System.Numerics;
using System.Runtime.ExceptionServices;

namespace SurvivalcraftTravelMap.Teleport;

public sealed class SafeTeleportService
{
    private static readonly TimeSpan ChunkLoadTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumDecorationScanDepth = 8;

    private readonly ITerrainAccess _terrain;
    private readonly IChunkLoader _chunkLoader;
    private readonly IPlayerMover _playerMover;
    private readonly IEntityCollisionQuery _collisionQuery;
    private readonly ITeleportClock _clock;
    private readonly ITeleportPositionCommitter _positionCommitter;
    private readonly Action<TeleportFailureDiagnostic> _reportFailure;
    private int _transactionActive;

    public SafeTeleportService(
        ITerrainAccess terrain,
        IChunkLoader chunkLoader,
        IPlayerMover playerMover,
        IEntityCollisionQuery collisionQuery,
        ITeleportClock clock)
        : this(
            terrain,
            chunkLoader,
            playerMover,
            collisionQuery,
            new DelegateTeleportPositionCommitter(static () => { }),
            clock,
            static _ => { })
    {
    }

    public SafeTeleportService(
        ITerrainAccess terrain,
        IChunkLoader chunkLoader,
        IPlayerMover playerMover,
        IEntityCollisionQuery collisionQuery,
        ITeleportClock clock,
        Action onPositionCommitted)
        : this(
            terrain,
            chunkLoader,
            playerMover,
            collisionQuery,
            new DelegateTeleportPositionCommitter(onPositionCommitted),
            clock,
            static _ => { })
    {
    }

    public SafeTeleportService(
        ITerrainAccess terrain,
        IChunkLoader chunkLoader,
        IPlayerMover playerMover,
        IEntityCollisionQuery collisionQuery,
        ITeleportClock clock,
        Action onPositionCommitted,
        Action<TeleportFailureDiagnostic> reportFailure)
        : this(
            terrain,
            chunkLoader,
            playerMover,
            collisionQuery,
            new DelegateTeleportPositionCommitter(onPositionCommitted),
            clock,
            reportFailure)
    {
    }

    internal SafeTeleportService(
        ITerrainAccess terrain,
        IChunkLoader chunkLoader,
        IPlayerMover playerMover,
        IEntityCollisionQuery collisionQuery,
        ITeleportPositionCommitter positionCommitter,
        ITeleportClock clock,
        Action<TeleportFailureDiagnostic> reportFailure)
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
        _positionCommitter = positionCommitter ?? throw new ArgumentNullException(nameof(positionCommitter));
        _reportFailure = reportFailure ?? (static _ => { });
    }

    public Task<TeleportResult> TeleportToSurfaceAsync(
        int x,
        int z,
        CancellationToken cancellationToken) =>
        TeleportToSurfaceAsync(x, z, static () => true, cancellationToken);

    internal async Task<TeleportResult> TeleportToSurfaceAsync(
        int x,
        int z,
        Func<bool> commitGuard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitGuard);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _transactionActive, 1, 0) != 0)
        {
            return TeleportResult.Busy;
        }

        var trace = new TeleportExecutionTrace();
        try
        {
            return await TeleportToSurfaceCoreAsync(
                x,
                z,
                commitGuard,
                cancellationToken,
                trace).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(trace, exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _transactionActive, 0);
        }
    }

    private async Task<TeleportResult> TeleportToSurfaceCoreAsync(
        int x,
        int z,
        Func<bool> commitGuard,
        CancellationToken cancellationToken,
        TeleportExecutionTrace trace)
    {
        trace.Stage = TeleportExecutionStage.CandidateSearch;
        if (!IsColumnInWorld(x, z, cancellationToken))
        {
            return TeleportResult.OutOfWorld;
        }

        trace.Stage = TeleportExecutionStage.ChunkLoad;
        var chunkLease = await LoadChunksAsync(x, z, cancellationToken).ConfigureAwait(false);
        if (chunkLease is null)
        {
            return TeleportResult.ChunkTimeout;
        }

        using (chunkLease)
        {
            trace.Stage = TeleportExecutionStage.CandidateSearch;
            TeleportCandidate? best = null;
            var bestScore = int.MinValue;
            long currentDistance = -1;
            foreach (var column in TeleportCandidate.GenerateSurface(x, z))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var distance = HorizontalDistanceSquared(column, x, z);
                if (distance != currentDistance)
                {
                    if (best.HasValue)
                    {
                        return await MoveTransactionallyAsync(
                            best.Value,
                            commitGuard,
                            cancellationToken,
                            trace).ConfigureAwait(false);
                    }

                    currentDistance = distance;
                    bestScore = int.MinValue;
                }

                if (!IsColumnInWorld(column.X, column.Z, cancellationToken))
                {
                    continue;
                }

                var assessment = AssessSurfaceCandidate(
                    column.X,
                    column.Z,
                    cancellationToken,
                    out var candidate);
                if (assessment.IsSafe)
                {
                    var score = GetSurroundingSafetyScore(candidate, cancellationToken);
                    if (!best.HasValue || score > bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
            }

            if (best.HasValue)
            {
                return await MoveTransactionallyAsync(
                    best.Value,
                    commitGuard,
                    cancellationToken,
                    trace).ConfigureAwait(false);
            }

            return TeleportResult.NoSafePosition;
        }
    }

    public Task<TeleportResult> TeleportToWaypointAsync(
        Vector3 xyz,
        CancellationToken cancellationToken) =>
        TeleportToWaypointAsync(xyz, static () => true, cancellationToken);

    internal async Task<TeleportResult> TeleportToWaypointAsync(
        Vector3 xyz,
        Func<bool> commitGuard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitGuard);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _transactionActive, 1, 0) != 0)
        {
            return TeleportResult.Busy;
        }

        var trace = new TeleportExecutionTrace();
        try
        {
            return await TeleportToWaypointCoreAsync(
                xyz,
                commitGuard,
                cancellationToken,
                trace).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(trace, exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _transactionActive, 0);
        }
    }

    private async Task<TeleportResult> TeleportToWaypointCoreAsync(
        Vector3 xyz,
        Func<bool> commitGuard,
        CancellationToken cancellationToken,
        TeleportExecutionTrace trace)
    {
        trace.Stage = TeleportExecutionStage.CandidateSearch;
        if (!TryFloorToInt(xyz.X, out var x)
            || !TryFloorToInt(xyz.Y, out var y)
            || !TryFloorToInt(xyz.Z, out var z)
            || !IsColumnInWorld(x, z, cancellationToken)
            || !IsCellInWorld(x, y, z, cancellationToken))
        {
            return TeleportResult.OutOfWorld;
        }

        trace.Stage = TeleportExecutionStage.ChunkLoad;
        var chunkLease = await LoadChunksAsync(x, z, cancellationToken).ConfigureAwait(false);
        if (chunkLease is null)
        {
            return TeleportResult.ChunkTimeout;
        }

        using (chunkLease)
        {
            trace.Stage = TeleportExecutionStage.CandidateSearch;
            TeleportCandidate? best = null;
            (int Safety, int VerticalCloseness) bestScore = (int.MinValue, int.MinValue);
            long currentDistance = -1;
            foreach (var candidate in TeleportCandidate.GenerateWaypoint(x, y, z))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var distance = HorizontalDistanceSquared(candidate, x, z);
                if (distance != currentDistance)
                {
                    if (best.HasValue)
                    {
                        return await MoveTransactionallyAsync(
                            best.Value,
                            commitGuard,
                            cancellationToken,
                            trace).ConfigureAwait(false);
                    }

                    currentDistance = distance;
                    bestScore = (int.MinValue, int.MinValue);
                }

                if (AssessCandidate(candidate, cancellationToken).IsSafe)
                {
                    var score = (
                        GetSurroundingSafetyScore(candidate, cancellationToken),
                        -Math.Abs(candidate.Y!.Value - y));
                    if (!best.HasValue || score.CompareTo(bestScore) > 0)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
            }

            if (best.HasValue)
            {
                return await MoveTransactionallyAsync(
                    best.Value,
                    commitGuard,
                    cancellationToken,
                    trace).ConfigureAwait(false);
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

    private CandidateAssessment AssessSurfaceCandidate(
        int x,
        int z,
        CancellationToken cancellationToken,
        out TeleportCandidate candidate)
    {
        candidate = default;
        var surfaceY = GetSurfaceHeight(x, z, cancellationToken);
        for (var decorationDepth = 0; decorationDepth <= MaximumDecorationScanDepth; decorationDepth++)
        {
            var supportYLong = (long)surfaceY - decorationDepth;
            if (supportYLong is < int.MinValue or > int.MaxValue)
            {
                return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
            }

            var supportY = (int)supportYLong;
            if (!IsCellInWorld(x, supportY, z, cancellationToken))
            {
                return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
            }

            var supportKind = GetBlockKind(x, supportY, z, cancellationToken);
            if (supportKind is TeleportBlockKind.Air or TeleportBlockKind.Passable)
            {
                continue;
            }

            var feetYLong = supportYLong + 1L;
            if (feetYLong is < int.MinValue or > int.MaxValue)
            {
                return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
            }

            candidate = new TeleportCandidate(x, (int)feetYLong, z);
            return AssessCandidate(candidate, cancellationToken);
        }

        return CandidateAssessment.Reject(TeleportCandidateRejectionReason.NoSupport);
    }

    private CandidateAssessment AssessCandidate(
        TeleportCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Y is not { } feetY)
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
        }

        var groundYLong = (long)feetY - 1L;
        var headYLong = (long)feetY + 1L;
        if (groundYLong is < int.MinValue or > int.MaxValue
            || headYLong is < int.MinValue or > int.MaxValue)
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
        }

        var groundY = (int)groundYLong;
        var headY = (int)headYLong;
        if (!IsColumnInWorld(candidate.X, candidate.Z, cancellationToken))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
        }

        if (!IsCellInWorld(candidate.X, groundY, candidate.Z, cancellationToken)
            || !IsCellInWorld(candidate.X, feetY, candidate.Z, cancellationToken)
            || !IsCellInWorld(candidate.X, headY, candidate.Z, cancellationToken))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.OutOfWorld);
        }

        var groundKind = GetBlockKind(candidate.X, groundY, candidate.Z, cancellationToken);
        var feetKind = GetBlockKind(candidate.X, feetY, candidate.Z, cancellationToken);
        var headKind = GetBlockKind(candidate.X, headY, candidate.Z, cancellationToken);
        if (TeleportBlockSafetyPolicy.IsHarmful(groundKind)
            || TeleportBlockSafetyPolicy.IsHarmful(feetKind)
            || TeleportBlockSafetyPolicy.IsHarmful(headKind))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.HarmfulContent);
        }

        if (!TeleportBlockSafetyPolicy.IsStableSupport(groundKind)
            && !TeleportBlockSafetyPolicy.IsWater(groundKind))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.NoSupport);
        }

        if (!TeleportBlockSafetyPolicy.IsFeetPassable(feetKind))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.BlockedBody);
        }

        if (!TeleportBlockSafetyPolicy.IsHeadBreathable(headKind))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.NonBreathableHead);
        }

        var position = GetPosition(candidate.X, feetY, candidate.Z);
        if (HasBlockingCollisionExcludingPlayer(position, cancellationToken))
        {
            return CandidateAssessment.Reject(TeleportCandidateRejectionReason.EntityCollision);
        }

        return CandidateAssessment.Safe;
    }

    private int GetSurroundingSafetyScore(
        TeleportCandidate candidate,
        CancellationToken cancellationToken)
    {
        var feetY = candidate.Y!.Value;
        var score = 0;
        for (var offsetX = -1; offsetX <= 1; offsetX++)
        {
            for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                if (offsetX == 0 && offsetZ == 0)
                {
                    continue;
                }

                var xLong = (long)candidate.X + offsetX;
                var zLong = (long)candidate.Z + offsetZ;
                var groundLong = (long)feetY - 1;
                var headLong = (long)feetY + 1;
                if (xLong is < int.MinValue or > int.MaxValue
                    || zLong is < int.MinValue or > int.MaxValue
                    || groundLong is < int.MinValue or > int.MaxValue
                    || headLong is < int.MinValue or > int.MaxValue)
                {
                    continue;
                }

                var neighborX = (int)xLong;
                var neighborZ = (int)zLong;
                var neighbor = new TeleportCandidate(neighborX, feetY, neighborZ);
                if (AssessCandidate(neighbor, cancellationToken).IsSafe)
                {
                    score++;
                }
            }
        }

        return score;
    }

    private static long HorizontalDistanceSquared(
        TeleportCandidate candidate,
        int targetX,
        int targetZ)
    {
        var deltaX = (long)candidate.X - targetX;
        var deltaZ = (long)candidate.Z - targetZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private async Task<TeleportResult> MoveTransactionallyAsync(
        TeleportCandidate candidate,
        Func<bool> commitGuard,
        CancellationToken cancellationToken,
        TeleportExecutionTrace trace)
    {
        var feetY = candidate.Y!.Value;
        cancellationToken.ThrowIfCancellationRequested();
        trace.Stage = TeleportExecutionStage.MovementSnapshot;
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
            trace.Stage = TeleportExecutionStage.PositionWrite;
            _playerMover.Move(movement);
            cancellationToken.ThrowIfCancellationRequested();
            trace.Stage = TeleportExecutionStage.PostMoveValidation;
            await _clock.WaitForNextUpdateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!AssessCandidate(candidate, cancellationToken).IsSafe)
            {
                throw new UnsafePostMoveValidationException();
            }

            trace.Stage = TeleportExecutionStage.PositionSync;
            _positionCommitter.Commit(commitGuard);
            return TeleportResult.Success;
        }
        catch (Exception originalFailure)
        {
            var originalStage = trace.Stage;
            trace.Stage = TeleportExecutionStage.Rollback;
            RestoreOrThrow(CreateSafeRollbackSnapshot(snapshot), originalFailure);
            trace.Stage = originalStage;
            cancellationToken.ThrowIfCancellationRequested();
            if (originalFailure is UnsafePostMoveValidationException)
            {
                return TeleportResult.RolledBack;
            }

            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw;
        }
    }

    private void ReportFailure(TeleportExecutionTrace trace, Exception exception)
    {
        try
        {
            _reportFailure(new TeleportFailureDiagnostic(trace.Stage, exception));
        }
        catch
        {
            // Diagnostics must never replace the teleport failure being reported.
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

    private readonly record struct CandidateAssessment(
        bool IsSafe,
        TeleportCandidateRejectionReason? Rejection)
    {
        public static CandidateAssessment Safe { get; } = new(true, null);

        public static CandidateAssessment Reject(TeleportCandidateRejectionReason reason) =>
            new(false, reason);
    }

    private sealed class DelegateTeleportPositionCommitter(Action? commit) : ITeleportPositionCommitter
    {
        private readonly Action _commit = commit ?? (static () => { });

        public void Commit(Func<bool> commitGuard)
        {
            ArgumentNullException.ThrowIfNull(commitGuard);
            if (!commitGuard())
            {
                throw new OperationCanceledException(
                    "The teleport commit guard rejected authoritative position synchronization.");
            }

            _commit();
        }
    }

    private sealed class TeleportExecutionTrace
    {
        internal TeleportExecutionStage Stage { get; set; } = TeleportExecutionStage.CandidateSearch;
    }
}
