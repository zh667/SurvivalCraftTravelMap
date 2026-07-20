namespace SurvivalcraftTravelMap.Map;

internal enum ExplorationFailureOperation
{
    Record,
    CoverageLookup,
}

internal sealed class ExplorationFailureReporter
{
    private readonly Action<string> _warningSink;
    private readonly HashSet<(TerrainChunkCoordinate Chunk, ExplorationFailureOperation Operation, string ErrorSignature)> _warnings = [];

    public ExplorationFailureReporter(Action<string> warningSink)
    {
        ArgumentNullException.ThrowIfNull(warningSink);
        _warningSink = warningSink;
    }

    public void Report(
        TerrainChunkCoordinate chunk,
        ExplorationFailureOperation operation,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var errorSignature = $"{exception.GetType().FullName}: {exception.Message}";
        if (!_warnings.Add((chunk, operation, errorSignature)))
            return;

        var operationText = operation switch
        {
            ExplorationFailureOperation.Record => "exploration failed",
            ExplorationFailureOperation.CoverageLookup => "exploration coverage lookup failed",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        _warningSink(
            $"[TravelMap] Terrain chunk ({chunk.X}, {chunk.Z}) {operationText}: {errorSignature}");
    }

    public void Clear()
    {
        _warnings.Clear();
    }
}

internal sealed class ExplorationCoverageProbe
{
    private readonly Func<TerrainChunkCoordinate, bool> _isFullyExplored;
    private readonly ExplorationFailureReporter _failureReporter;

    public ExplorationCoverageProbe(
        Func<TerrainChunkCoordinate, bool> isFullyExplored,
        ExplorationFailureReporter failureReporter)
    {
        ArgumentNullException.ThrowIfNull(isFullyExplored);
        ArgumentNullException.ThrowIfNull(failureReporter);
        _isFullyExplored = isFullyExplored;
        _failureReporter = failureReporter;
        IsFullyExplored = TryIsFullyExplored;
    }

    public Func<TerrainChunkCoordinate, bool> IsFullyExplored { get; }

    private bool TryIsFullyExplored(TerrainChunkCoordinate chunk)
    {
        try
        {
            return _isFullyExplored(chunk);
        }
        catch (Exception exception)
        {
            _failureReporter.Report(
                chunk,
                ExplorationFailureOperation.CoverageLookup,
                exception);
            return false;
        }
    }
}
