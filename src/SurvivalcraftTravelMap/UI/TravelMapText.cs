using Game;

namespace SurvivalcraftTravelMap.UI;

internal static class TravelMapText
{
    public static string Get(string key, string fallback)
    {
        var text = LanguageControl.Get(out var found, "TravelMap", key);
        return found ? text : fallback;
    }
}
