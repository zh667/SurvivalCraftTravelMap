using SurvivalcraftTravelMap.Map;
using NVector3 = System.Numerics.Vector3;

namespace SurvivalcraftTravelMap.UI;

public enum CreatureMapMarkerKind
{
    Hostile,
    Ally,
    Animal,
    Bird,
    Fish,
}

public readonly record struct CreatureMapMarker(
    NVector3 Position,
    CreatureMapMarkerKind Kind);

public static class CreatureMapMarkerStyle
{
    public static readonly Rgba32 HostileColor = new(255, 60, 60, byte.MaxValue);
    public static readonly Rgba32 AllyColor = new(70, 140, 255, byte.MaxValue);
    public static readonly Rgba32 AnimalColor = new(60, 220, 80, byte.MaxValue);
    public static readonly Rgba32 BirdColor = new(255, 220, 60, byte.MaxValue);
    public static readonly Rgba32 FishColor = new(60, 210, 230, byte.MaxValue);
    public static readonly Rgba32 OutlineColor = new(18, 18, 18, byte.MaxValue);

    public static Rgba32 ColorFor(CreatureMapMarkerKind kind) => kind switch
    {
        CreatureMapMarkerKind.Hostile => HostileColor,
        CreatureMapMarkerKind.Ally => AllyColor,
        CreatureMapMarkerKind.Bird => BirdColor,
        CreatureMapMarkerKind.Fish => FishColor,
        _ => AnimalColor,
    };

    /// <summary>
    /// Marker size relative to the creature-marker size setting: threats stand out, while birds
    /// and fish (which come in flocks and shoals) stay small enough not to blot out the terrain.
    /// </summary>
    public static float SizeFactorFor(CreatureMapMarkerKind kind) => kind switch
    {
        CreatureMapMarkerKind.Hostile => 1.25f,
        CreatureMapMarkerKind.Bird or CreatureMapMarkerKind.Fish => 0.6f,
        _ => 1f,
    };
}

/// <summary>What the classifier needs to know about a creature, read once from its entity.</summary>
/// <param name="IsPredator">Its creature category is LandPredator or WaterPredator.</param>
/// <param name="IsAquatic">Its creature category is WaterPredator or WaterOther.</param>
/// <param name="ChasesPlayer">
/// It has the game's chase behaviour, allowed to attack the player, with a non-zero day or night
/// chase range: the game's own notion of a creature that comes after the player.
/// </param>
/// <param name="Flies">Its locomotion has a fly speed.</param>
/// <param name="ComponentTypeNames">Type names of its components, for mods known by name.</param>
public readonly record struct CreatureMarkerTraits(
    bool IsPredator,
    bool IsAquatic,
    bool ChasesPlayer,
    bool Flies,
    IReadOnlyCollection<string> ComponentTypeNames);

/// <summary>
/// Sorts creatures into marker kinds by what they do rather than by their creature category
/// alone, which mods fill in loosely: the Ghoul mod registers every ghoul (and its shooting
/// corpse head) as a bird, Wonderful Era its butterflies, locusts and bats, the CS mod its enemies,
/// teammates and hostages alike as LandOther.
/// </summary>
public static class CreatureMarkerClassifier
{
    // Units whose hostility or friendship lives in mod code the chase behaviour does not show.
    private static readonly string[] HostileComponents =
    [
        "ComponentTacticalEnemy",   // CS weapons: the bandits
        "ComponentMDRobot",         // Mech Defense: the raiding robots
    ];

    private static readonly string[] AllyComponents =
    [
        "ComponentTacticalCompanion",   // CS weapons: teammates and hostages
    ];

    public static CreatureMapMarkerKind Classify(CreatureMarkerTraits traits)
    {
        if (HasAny(traits.ComponentTypeNames, HostileComponents)
            || traits.ChasesPlayer
            || traits.IsPredator)
        {
            return CreatureMapMarkerKind.Hostile;
        }

        if (HasAny(traits.ComponentTypeNames, AllyComponents))
        {
            return CreatureMapMarkerKind.Ally;
        }

        if (traits.IsAquatic)
        {
            return CreatureMapMarkerKind.Fish;
        }

        return traits.Flies ? CreatureMapMarkerKind.Bird : CreatureMapMarkerKind.Animal;
    }

    private static bool HasAny(IReadOnlyCollection<string> names, string[] wanted)
    {
        foreach (var name in names)
        {
            if (Array.IndexOf(wanted, name) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
