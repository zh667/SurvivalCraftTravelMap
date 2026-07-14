using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Map;

public enum ExplorationRecordResult
{
    Recorded,
    NotReady,
    Pressure,
}

public sealed class ExplorationRecorder(
    TerrainMapSampler sampler,
    ExplorationTileStore tileStore)
{
    private readonly TerrainMapSampler _sampler = sampler
        ?? throw new ArgumentNullException(nameof(sampler));
    private readonly ExplorationTileStore _tileStore = tileStore
        ?? throw new ArgumentNullException(nameof(tileStore));

    public bool IsChunkFullyExplored(TerrainChunkCoordinate chunk)
    {
        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        return _tileStore.IsRegionFullyExplored(
            coordinate.TileX,
            coordinate.TileZ,
            coordinate.LocalX,
            coordinate.LocalZ,
            TerrainChunkCoordinate.Size,
            TerrainChunkCoordinate.Size);
    }

    public ExplorationRecordResult RecordChunk(TerrainChunkCoordinate chunk)
    {
        Span<Rgba32> colors = stackalloc Rgba32[TerrainChunkCoordinate.PixelCount];
        if (!_sampler.TrySampleChunk(chunk, colors))
        {
            return ExplorationRecordResult.NotReady;
        }

        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        if (_tileStore.TryAcquireMutation(coordinate.TileX, coordinate.TileZ, out var lease)
            == TileMutationAdmission.Pressure)
        {
            return ExplorationRecordResult.Pressure;
        }

        var admittedLease = lease
            ?? throw new InvalidOperationException("Mutation admission returned no lease.");
        using (admittedLease)
        {
            admittedLease.Tile.SetRegion(
                coordinate.LocalX,
                coordinate.LocalZ,
                TerrainChunkCoordinate.Size,
                TerrainChunkCoordinate.Size,
                colors);
        }

        return ExplorationRecordResult.Recorded;
    }
}
