using System.Globalization;
using System.Numerics;

namespace SurvivalcraftTravelMap.Waypoints;

/// <summary>
/// Stable identity of a single death record, formed from the record's day stamp and its exact
/// world location. Two identities are equal when the game supplied the same stored record, so the
/// mod can remember which death the player dismissed without ever mutating the native history.
/// </summary>
public readonly record struct DeathMarkerIdentity(double Day, float X, float Y, float Z)
{
    public static DeathMarkerIdentity FromLocation(double day, Vector3 location) =>
        new(day, location.X, location.Y, location.Z);

    /// <summary>Serializes the identity as <c>Day,X,Y,Z</c> using round-trippable invariant text.</summary>
    public string Serialize() => string.Join(
        ',',
        Day.ToString("R", CultureInfo.InvariantCulture),
        X.ToString("R", CultureInfo.InvariantCulture),
        Y.ToString("R", CultureInfo.InvariantCulture),
        Z.ToString("R", CultureInfo.InvariantCulture));

    public static bool TryParse(string? text, out DeathMarkerIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',');
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var day)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        identity = new DeathMarkerIdentity(day, x, y, z);
        return true;
    }
}
