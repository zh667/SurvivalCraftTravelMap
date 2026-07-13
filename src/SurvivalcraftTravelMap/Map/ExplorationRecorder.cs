using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Map;

public sealed class ExplorationRecorder(
    TerrainMapSampler sampler,
    ExplorationTileStore tileStore)
{
    private readonly TerrainMapSampler _sampler = sampler
        ?? throw new ArgumentNullException(nameof(sampler));
    private readonly ExplorationTileStore _tileStore = tileStore
        ?? throw new ArgumentNullException(nameof(tileStore));

    public void RecordVisibleArea(int centerX, int centerZ, int radius)
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

        try
        {
            for (var z = minimumZ; ; z++)
            {
                for (var x = minimumX; ; x++)
                {
                    var color = _sampler.Sample(x, z);
                    var coordinate = TileCoordinate.FromWorld(x, z);
                    var key = (X: coordinate.TileX, Z: coordinate.TileZ);
                    if (!leases.TryGetValue(key, out var lease))
                    {
                        lease = _tileStore.AcquireMutation(key.X, key.Z);
                        leases.Add(key, lease);
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
    }
}
