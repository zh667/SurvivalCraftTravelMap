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
        var touchedTiles = new HashSet<(int X, int Z)>();

        for (var z = minimumZ; ; z++)
        {
            for (var x = minimumX; ; x++)
            {
                var color = _sampler.Sample(x, z);
                var coordinate = TileCoordinate.FromWorld(x, z);
                var tile = _tileStore.GetOrLoad(coordinate.TileX, coordinate.TileZ);
                if (touchedTiles.Add((coordinate.TileX, coordinate.TileZ)))
                {
                    _tileStore.MarkDirty(tile);
                }

                tile.SetPixel(coordinate.LocalX, coordinate.LocalZ, color);

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
}
