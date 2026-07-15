using SurvivalcraftTravelMap.Map;
using NVector3 = System.Numerics.Vector3;

namespace SurvivalcraftTravelMap.UI;

public enum CreatureMapMarkerKind
{
    Predator,
    Bird,
    Other,
}

public readonly record struct CreatureMapMarker(
    NVector3 Position,
    CreatureMapMarkerKind Kind);

public static class CreatureMapMarkerStyle
{
    public static readonly Rgba32 PredatorColor = new(255, 60, 60, byte.MaxValue);
    public static readonly Rgba32 BirdColor = new(255, 220, 60, byte.MaxValue);
    public static readonly Rgba32 OtherColor = new(60, 220, 80, byte.MaxValue);
    public static readonly Rgba32 OutlineColor = new(18, 18, 18, byte.MaxValue);

    public static Rgba32 ColorFor(CreatureMapMarkerKind kind) => kind switch
    {
        CreatureMapMarkerKind.Predator => PredatorColor,
        CreatureMapMarkerKind.Bird => BirdColor,
        _ => OtherColor,
    };
}
