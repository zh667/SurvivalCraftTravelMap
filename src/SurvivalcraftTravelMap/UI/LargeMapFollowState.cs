using System.Numerics;
using SurvivalcraftTravelMap.Map;

namespace SurvivalcraftTravelMap.UI;

internal sealed class LargeMapFollowState
{
    public bool IsFollowing { get; private set; }

    public MapTransform Locate(MapTransform transform, Vector2 playerPosition)
    {
        IsFollowing = true;
        return transform with { Center = playerPosition };
    }

    public MapTransform LocateTarget(MapTransform transform, Vector2 targetPosition)
    {
        IsFollowing = false;
        return transform with { Center = targetPosition };
    }

    public MapTransform Update(MapTransform transform, Vector2 playerPosition) =>
        IsFollowing ? transform with { Center = playerPosition } : transform;

    public void ObserveManualNavigation(TravelMapUiCommandKind commandKind)
    {
        if (commandKind is TravelMapUiCommandKind.Pan or TravelMapUiCommandKind.Zoom)
        {
            IsFollowing = false;
        }
    }
}
