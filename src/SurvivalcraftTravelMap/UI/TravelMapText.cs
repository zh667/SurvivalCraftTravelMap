using Game;

namespace SurvivalcraftTravelMap.UI;

internal static class TravelMapText
{
    public static string Get(string key, string fallback)
    {
        var text = LanguageControl.Get(out var found, "TravelMap", key);
        return found ? text : fallback;
    }

    public static string CompassDirection(CompassDirection direction) => direction switch
    {
        global::SurvivalcraftTravelMap.UI.CompassDirection.North => Get("compassNorth", "北"),
        global::SurvivalcraftTravelMap.UI.CompassDirection.East => Get("compassEast", "东"),
        global::SurvivalcraftTravelMap.UI.CompassDirection.South => Get("compassSouth", "南"),
        global::SurvivalcraftTravelMap.UI.CompassDirection.West => Get("compassWest", "西"),
        _ => string.Empty,
    };
}
