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

/// <summary>
/// Identifies which world layer a map view is showing. Cached terrain has to be repainted from
/// scratch when this changes, because two layers reuse the same map tiles with unrelated content
/// and so cannot be told apart by tile version stamps.
/// </summary>
internal readonly record struct MapLayerIdentity(MapViewMode Mode, int CaveY)
{
    /// <summary>
    /// The cave depth follows the player's Y every frame, including while the surface view is the
    /// one on screen — so it only forms part of the identity when the cave view is being drawn.
    /// </summary>
    public static MapLayerIdentity For(MapViewMode mode, int caveY) => mode == MapViewMode.Cave
        ? new MapLayerIdentity(MapViewMode.Cave, caveY)
        : new MapLayerIdentity(MapViewMode.Surface, 0);
}

internal sealed class MapViewPixelSource(
    IExploredMapPixelSource surface,
    Func<MapViewMode> mode,
    Func<IExploredMapPixelSource> cave) :
    IExploredMapTileIndexSource,
    IExploredMapLodSource,
    IBoundedExploredMapTileIndexSource,
    IExploredMapTileVersionSource
{
    private IExploredMapPixelSource Active => mode() == MapViewMode.Cave ? cave() : surface;

    // The mini map's texture cache treats a source without version stamps as immutable after the
    // initial fill (refresh only on scroll/reset), so this wrapper must forward the active
    // source's stamps or live exploration never appears until a scroll or a rejoin. The stamps
    // are salted with the active source's identity so a surface/cave (or cave-layer) switch can
    // never alias two sources' version spaces and skip a repaint.
    long IExploredMapTileVersionSource.MutationVersion
    {
        get
        {
            var active = Active;
            return active is IExploredMapTileVersionSource versioned
                ? IdentitySalt(active) ^ versioned.MutationVersion
                : IdentitySalt(active);
        }
    }

    long IExploredMapTileVersionSource.GetTileMutationVersion(int tileX, int tileZ)
    {
        var active = Active;
        return active is IExploredMapTileVersionSource versioned
            ? IdentitySalt(active) ^ versioned.GetTileMutationVersion(tileX, tileZ)
            : IdentitySalt(active);
    }

    private static long IdentitySalt(IExploredMapPixelSource active) =>
        (long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(active) << 32;

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
