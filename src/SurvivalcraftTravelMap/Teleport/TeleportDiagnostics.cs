namespace SurvivalcraftTravelMap.Teleport;

public enum TeleportExecutionStage
{
    ProtocolDispatch,
    ChunkLoad,
    CandidateSearch,
    MovementSnapshot,
    PositionWrite,
    PostMoveValidation,
    Rollback,
    PositionSync,
}

public sealed record TeleportFailureDiagnostic(
    TeleportExecutionStage Stage,
    Exception Exception);
