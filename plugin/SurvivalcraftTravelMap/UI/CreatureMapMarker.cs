using SurvivalcraftTravelMap.Map;
using NVector3 = System.Numerics.Vector3;

namespace SurvivalcraftTravelMap.UI;

/// <summary>How a creature stands towards the player; shown as the marker's colour.</summary>
public enum CreatureMarkerAttitude
{
    /// <summary>Red: chasing the player right now, or a known mod enemy.</summary>
    Chasing,

    /// <summary>Yellow: does not chase yet but may (by range, on touch or when hit), or a predator.</summary>
    Provokable,

    /// <summary>Green: leaves the player alone.</summary>
    Passive,

    /// <summary>Blue: a known mod companion or a saddled (tamed) mount.</summary>
    Friendly,
}

/// <summary>How a creature moves; shown as the marker's size and covered by the bird/fish settings.</summary>
public enum CreatureMarkerBody
{
    Land,
    Bird,
    Fish,
}

public readonly record struct CreatureMapMarkerKind(
    CreatureMarkerAttitude Attitude,
    CreatureMarkerBody Body)
{
    public override string ToString() => $"{Attitude}/{Body}";
}

public readonly record struct CreatureMapMarker(
    NVector3 Position,
    CreatureMapMarkerKind Kind);

public static class CreatureMapMarkerStyle
{
    public static readonly Rgba32 ChasingColor = new(255, 60, 60, byte.MaxValue);
    public static readonly Rgba32 ProvokableColor = new(255, 220, 60, byte.MaxValue);
    public static readonly Rgba32 PassiveColor = new(60, 220, 80, byte.MaxValue);
    public static readonly Rgba32 FriendlyColor = new(70, 140, 255, byte.MaxValue);
    public static readonly Rgba32 OutlineColor = new(18, 18, 18, byte.MaxValue);

    public static Rgba32 ColorFor(CreatureMarkerAttitude attitude) => attitude switch
    {
        CreatureMarkerAttitude.Chasing => ChasingColor,
        CreatureMarkerAttitude.Provokable => ProvokableColor,
        CreatureMarkerAttitude.Friendly => FriendlyColor,
        _ => PassiveColor,
    };

    public static Rgba32 ColorFor(CreatureMapMarkerKind kind) => ColorFor(kind.Attitude);

    /// <summary>
    /// Marker size relative to the creature-marker size setting: red threats stand out whatever
    /// they are, while birds and fish (which come in flocks and shoals) stay small enough not to
    /// blot out the terrain.
    /// </summary>
    public static float SizeFactorFor(CreatureMapMarkerKind kind) => kind switch
    {
        { Attitude: CreatureMarkerAttitude.Chasing } => 1.25f,
        { Body: CreatureMarkerBody.Bird or CreatureMarkerBody.Fish } => 0.6f,
        _ => 1f,
    };
}

/// <summary>What the classifier needs to know about a creature, read once from its entity.</summary>
/// <param name="IsPredator">Its creature category is LandPredator or WaterPredator.</param>
/// <param name="IsAquatic">Its creature category is WaterPredator or WaterOther.</param>
/// <param name="Flies">Its locomotion has a fly speed.</param>
/// <param name="ComponentTypeNames">Type names of its components, for mods known by name.</param>
/// <param name="DayChaseRange">The game's chase behaviour: range it comes after the player by day (0 without one).</param>
/// <param name="NightChaseRange">Same, by night.</param>
/// <param name="ChaseOnTouchProbability">Chance it gives chase when bumped into.</param>
/// <param name="ChaseWhenAttackedProbability">Chance it fights back when hit.</param>
/// <param name="AttacksPlayer">
/// The plugin edition's chase behaviour may be told never to attack players (mods set it); the
/// multiplayer edition has no such switch, so it is always true there.
/// </param>
/// <param name="TemplateName">Its entity template (database object) name, such as <c>Horse_Bay_Saddled</c>.</param>
public readonly record struct CreatureMarkerTraits(
    bool IsPredator,
    bool IsAquatic,
    bool Flies,
    IReadOnlyCollection<string> ComponentTypeNames,
    float DayChaseRange = 0f,
    float NightChaseRange = 0f,
    float ChaseOnTouchProbability = 0f,
    float ChaseWhenAttackedProbability = 0f,
    bool AttacksPlayer = true,
    string TemplateName = "");

/// <summary>
/// Sorts creatures by what they do to the player rather than by their creature category alone,
/// which mods fill in loosely: the Ghoul mod registers every ghoul (and its shooting corpse head)
/// as a bird, Wonderful Era its butterflies, locusts and bats, the CS mod its enemies, teammates
/// and hostages alike as LandOther. Whether a creature is chasing the player right now is not
/// part of this: the map checks that every frame and shows such a creature as
/// <see cref="CreatureMarkerAttitude.Chasing"/>.
/// </summary>
public static class CreatureMarkerClassifier
{
    // A creature that fights back at least every other hit counts as one to keep away from;
    // cows (0.1) and camels (0.2) rarely do.
    public const float FightsBackProbability = 0.5f;

    // Units whose hostility or friendship lives in mod code the chase behaviour does not show.
    private static readonly string[] HostileComponents =
    [
        "ComponentTacticalEnemy",   // CS weapons: the bandits
        "ComponentMDRobot",         // Mech Defense: the raiding robots
    ];

    private static readonly string[] FriendlyComponents =
    [
        "ComponentTacticalCompanion",   // CS weapons: teammates and hostages
    ];

    // The game itself tells a tamed mount from a wild one by this template suffix
    // (ComponentDumpRiderBehavior); wild donkeys and camels carry a rider component too.
    private const string SaddledSuffix = "_Saddled";

    public static CreatureMapMarkerKind Classify(CreatureMarkerTraits traits) =>
        new(ClassifyAttitude(traits), ClassifyBody(traits));

    public static CreatureMarkerAttitude ClassifyAttitude(CreatureMarkerTraits traits)
    {
        // Known mod enemies: their code does not show whether they are after the player now.
        if (HasAny(traits.ComponentTypeNames, HostileComponents))
        {
            return CreatureMarkerAttitude.Chasing;
        }

        if (HasAny(traits.ComponentTypeNames, FriendlyComponents)
            || (traits.TemplateName?.EndsWith(SaddledSuffix, StringComparison.Ordinal) ?? false))
        {
            return CreatureMarkerAttitude.Friendly;
        }

        // The game's chase behaviour goes after a player within its chase range or when bumped
        // into only if it may attack players; fighting back when hit does not check that.
        var chasesByRange = traits.AttacksPlayer
            && MathF.Max(traits.DayChaseRange, traits.NightChaseRange) > 0f;
        var chasesOnTouch = traits.AttacksPlayer && traits.ChaseOnTouchProbability > 0f;
        var fightsBack = traits.ChaseWhenAttackedProbability >= FightsBackProbability;
        return chasesByRange || chasesOnTouch || fightsBack || traits.IsPredator
            ? CreatureMarkerAttitude.Provokable
            : CreatureMarkerAttitude.Passive;
    }

    public static CreatureMarkerBody ClassifyBody(CreatureMarkerTraits traits)
    {
        if (traits.IsAquatic)
        {
            return CreatureMarkerBody.Fish;
        }

        return traits.Flies ? CreatureMarkerBody.Bird : CreatureMarkerBody.Land;
    }

    /// <summary>
    /// The marker for this frame: anything chasing the player (holding it as its target, or having
    /// hit it in the last seconds) shows red, allies included; a creature whose kind has hit the
    /// player before (<paramref name="isAttackerTemplate"/>) is at least yellow instead of green.
    /// </summary>
    public static CreatureMapMarkerKind ForFrame(
        CreatureMapMarkerKind kind,
        bool isChasingPlayer,
        bool isAttackerTemplate = false)
    {
        if (isChasingPlayer)
        {
            return kind with { Attitude = CreatureMarkerAttitude.Chasing };
        }

        return isAttackerTemplate && kind.Attitude == CreatureMarkerAttitude.Passive
            ? kind with { Attitude = CreatureMarkerAttitude.Provokable }
            : kind;
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
