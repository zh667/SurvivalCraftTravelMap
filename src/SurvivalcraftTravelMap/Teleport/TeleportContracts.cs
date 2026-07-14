using System.Numerics;

namespace SurvivalcraftTravelMap.Teleport;

public enum TeleportResult
{
    Success,
    ChunkTimeout,
    NoSafePosition,
    OutOfWorld,
    RolledBack,
    Busy,
}

public enum TeleportBlockKind
{
    Air,
    Passable,
    SafeSolid,
    Lava,
    Fire,
    Cactus,
    Spikes,
    Water,
    Fluid,
    Leaves,
    Falling,
    Damaging,
}

public readonly record struct PlayerMovementSnapshot(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 LinearVelocity,
    Vector3 AngularVelocity,
    float FallDistance,
    bool IsFalling,
    bool HasPendingFallDamage,
    object? NativeState = null);

public interface ITerrainAccess
{
    bool IsColumnInWorld(int x, int z);

    bool IsCellInWorld(int x, int y, int z);

    int GetSurfaceHeight(int x, int z);

    TeleportBlockKind GetBlockKind(int x, int y, int z);
}

public interface IChunkLoader
{
    Task<IChunkLoadLease> LoadAreaAsync(
        int centerX,
        int centerZ,
        int radius,
        CancellationToken cancellationToken);
}

public interface IChunkLoadLease : IDisposable;

public interface IPlayerMover
{
    PlayerMovementSnapshot CaptureSnapshot();

    void Move(PlayerMovementSnapshot movement);

    void Restore(PlayerMovementSnapshot snapshot);

    void RestoreSafely(PlayerMovementSnapshot snapshot);
}

public interface IEntityCollisionQuery
{
    /// <summary>
    /// Checks for blocking entities while excluding the player being teleported from the query.
    /// </summary>
    bool HasBlockingCollisionExcludingPlayer(Vector3 feetPosition);
}

public interface ITeleportClock
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);

    Task WaitForNextUpdateAsync(CancellationToken cancellationToken);
}

internal interface ITeleportPositionCommitter
{
    void Commit(Func<bool> commitGuard);
}

public sealed class TeleportRollbackException : Exception
{
    public TeleportRollbackException(Exception originalFailure, Exception restoreFailure)
        : base("Teleport failed and restoring the original player movement state also failed.",
            new AggregateException(originalFailure, restoreFailure))
    {
        ArgumentNullException.ThrowIfNull(originalFailure);
        ArgumentNullException.ThrowIfNull(restoreFailure);
        OriginalFailure = originalFailure;
        RestoreFailure = restoreFailure;
    }

    public Exception OriginalFailure { get; }

    public Exception RestoreFailure { get; }
}
