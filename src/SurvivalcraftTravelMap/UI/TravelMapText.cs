using Game;
using SurvivalcraftTravelMap.Settings;
using System.Globalization;

namespace SurvivalcraftTravelMap.UI;

internal static class TravelMapText
{
    public static string Get(string key, string fallback)
    {
        var text = LanguageControl.Get(out var found, "TravelMap", key);
        return found ? text : fallback;
    }

    public static string Format(string key, string fallback, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, fallback), arguments);

    public static string CompassDirection(CompassDirection direction) => direction switch
    {
        global::SurvivalcraftTravelMap.UI.CompassDirection.North => Get("compassNorth", "北"),
        global::SurvivalcraftTravelMap.UI.CompassDirection.East => Get("compassEast", "东"),
        global::SurvivalcraftTravelMap.UI.CompassDirection.South => Get("compassSouth", "南"),
        global::SurvivalcraftTravelMap.UI.CompassDirection.West => Get("compassWest", "西"),
        _ => string.Empty,
    };

    public static string MapShape(MapShape shape) => shape switch
    {
        global::SurvivalcraftTravelMap.Settings.MapShape.Circle => Get("mapShapeCircle", "圆形"),
        global::SurvivalcraftTravelMap.Settings.MapShape.Square => Get("mapShapeSquare", "方形"),
        global::SurvivalcraftTravelMap.Settings.MapShape.RoundedSquare => Get("mapShapeRoundedSquare", "圆角方形"),
        _ => string.Empty,
    };
}
