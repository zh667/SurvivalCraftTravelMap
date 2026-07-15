using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.UI;

public enum MapViewMode
{
    Surface,
    Cave,
}

public sealed class MapViewState
{
    public MapViewMode Mode { get; private set; }

    public int CaveY { get; private set; } = CaveLayer.CenterForY(64);

    public bool FollowsPlayerY { get; private set; } = true;

    public void ShowSurface() => Mode = MapViewMode.Surface;

    public void ShowCave(float playerY)
    {
        Mode = MapViewMode.Cave;
        if (FollowsPlayerY)
        {
            CaveY = CaveLayer.CenterForY(playerY);
        }
    }

    public void FollowPlayer(float playerY)
    {
        FollowsPlayerY = true;
        CaveY = CaveLayer.CenterForY(playerY);
    }

    public void StepCaveY(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        FollowsPlayerY = false;
        CaveY = CaveLayer.ClampCenter(CaveY + Math.Sign(direction));
    }

    public void SetCaveY(int y)
    {
        FollowsPlayerY = false;
        CaveY = CaveLayer.ClampCenter(y);
    }

    public void UpdatePlayerY(float playerY)
    {
        if (FollowsPlayerY)
        {
            CaveY = CaveLayer.CenterForY(playerY);
        }
    }
}

internal sealed class MapViewPixelSource(
    IExploredMapPixelSource surface,
    Func<MapViewMode> mode,
    Func<IExploredMapPixelSource> cave) :
    IExploredMapTileIndexSource,
    IExploredMapLodSource,
    IBoundedExploredMapTileIndexSource
{
    private IExploredMapPixelSource Active => mode() == MapViewMode.Cave ? cave() : surface;

    public IExploredMapReadSession BeginReadSession() => Active.BeginReadSession();

    public IReadOnlyList<MapTileCoordinate> GetKnownTiles(MapTileRegion region) =>
        Active is IExploredMapTileIndexSource indexed ? indexed.GetKnownTiles(region) : [];

    IExploredMapReadSession IExploredMapLodSource.BeginLodReadSession(
        IReadOnlyList<MapTileSamplePlan> plans,
        int stride,
        int maximumNewTiles) => Active is IExploredMapLodSource lod
            ? lod.BeginLodReadSession(plans, stride, maximumNewTiles)
            : Active.BeginReadSession();

    MapTileCatalog IBoundedExploredMapTileIndexSource.GetKnownTileCatalog(
        MapTileRegion region,
        int maximumCount) => Active is IBoundedExploredMapTileIndexSource bounded
            ? bounded.GetKnownTileCatalog(region, maximumCount)
            : new MapTileCatalog(GetKnownTiles(region).Take(maximumCount).ToArray(), IsTruncated: false);
}
