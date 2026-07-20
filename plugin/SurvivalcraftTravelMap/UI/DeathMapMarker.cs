using System.Numerics;

namespace SurvivalcraftTravelMap.UI;

public sealed record DeathMapMarker(
    Vector3 Position,
    double Day,
    string Cause);
