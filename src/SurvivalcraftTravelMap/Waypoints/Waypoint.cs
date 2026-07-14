using System.Numerics;

namespace SurvivalcraftTravelMap.Waypoints;

public sealed record Waypoint(
    Guid Id,
    string Name,
    Vector3 Position,
    DateTimeOffset CreatedAt);
