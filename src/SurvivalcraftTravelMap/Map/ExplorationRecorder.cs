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

    public ExplorationRecordResult RecordVisibleArea(int centerX, int centerZ, int radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }

        var minimumX = checked(centerX - radius);
        var maximumX = checked(centerX + radius);
        var minimumZ = checked(centerZ - radius);
        var maximumZ = checked(centerZ + radius);
        var leases = new Dictionary<(int X, int Z), ExplorationTileStore.MutationLease>();
        var pressure = false;

        try
        {
            for (var z = minimumZ; ; z++)
            {
                for (var x = minimumX; ; x++)
                {
                    if (!_sampler.TrySample(x, z, out var color))
                    {
                        if (x == maximumX)
                        {
                            break;
                        }

                        continue;
                    }

                    var coordinate = TileCoordinate.FromWorld(x, z);
                    var key = (X: coordinate.TileX, Z: coordinate.TileZ);
                    if (!leases.TryGetValue(key, out var lease))
                    {
                        if (_tileStore.TryAcquireMutation(key.X, key.Z, out lease)
                            == TileMutationAdmission.Pressure)
                        {
                            pressure = true;
                            if (x == maximumX)
                            {
                                break;
                            }

                            continue;
                        }

                        var admittedLease = lease!;
                        leases.Add(key, admittedLease);
                        lease = admittedLease;
                    }

                    lease.Tile.SetPixel(coordinate.LocalX, coordinate.LocalZ, color);

                    if (x == maximumX)
                    {
                        break;
                    }
                }

                if (z == maximumZ)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var lease in leases.Values)
            {
                lease.Dispose();
            }
        }

        return pressure ? ExplorationRecordResult.Pressure : ExplorationRecordResult.Recorded;
    }
}
