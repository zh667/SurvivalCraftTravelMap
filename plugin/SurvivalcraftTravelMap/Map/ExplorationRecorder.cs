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
            TerrainChunkCoordinate.Size)
            && _tileStore.IsRegionFullyHeightShaded(
                coordinate.TileX,
                coordinate.TileZ,
                coordinate.LocalX,
                coordinate.LocalZ,
                TerrainChunkCoordinate.Size,
                TerrainChunkCoordinate.Size)
            // Areas recorded before the map tracked seasons are recorded once more when the player
            // is near, so they too follow the seasons from then on.
            && _tileStore.IsRegionFullySeasonTracked(
                coordinate.TileX,
                coordinate.TileZ,
                coordinate.LocalX,
                coordinate.LocalZ,
                TerrainChunkCoordinate.Size,
                TerrainChunkCoordinate.Size)
            // Areas recorded before 2.9.0 show mirrored relief shading until recorded again with
            // the light from the screen's top-left.
            && _tileStore.IsChunkShadeCurrent(
                coordinate.TileX,
                coordinate.TileZ,
                coordinate.LocalX,
                coordinate.LocalZ);
    }

    public ExplorationRecordResult RecordChunk(TerrainChunkCoordinate chunk)
    {
        // Sampling the chunk scans 256 terrain columns, so the cheap admission probe comes first:
        // a refused tile used to burn a full sample every frame it stayed refused.
        var coordinate = TileCoordinate.FromWorld(chunk.OriginX, chunk.OriginZ);
        if (!_tileStore.CanAdmitMutation(coordinate.TileX, coordinate.TileZ))
        {
            return ExplorationRecordResult.Pressure;
        }

        Span<Rgba32> colors = stackalloc Rgba32[TerrainChunkCoordinate.PixelCount];
        Span<byte> heightShades = stackalloc byte[TerrainChunkCoordinate.PixelCount];
        Span<byte> seasons = stackalloc byte[TerrainChunkCoordinate.PixelCount];
        if (!_sampler.TrySampleChunk(chunk, colors, heightShades, seasons))
        {
            return ExplorationRecordResult.NotReady;
        }

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
                colors,
                heightShades,
                seasons);
        }

        return ExplorationRecordResult.Recorded;
    }
}
