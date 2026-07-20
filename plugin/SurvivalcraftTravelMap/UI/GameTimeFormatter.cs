using System.Globalization;

namespace SurvivalcraftTravelMap.UI;

public static class GameTimeFormatter
{
    public const int MinutesPerDay = 24 * 60;

    public static int GetDisplayedMinute(float timeOfDay)
    {
        if (!float.IsFinite(timeOfDay))
        {
            return 0;
        }

        var normalized = timeOfDay - MathF.Floor(timeOfDay);
        return Math.Clamp((int)MathF.Floor(normalized * MinutesPerDay), 0, MinutesPerDay - 1);
    }

    public static string FormatMinute(int minuteOfDay)
    {
        var normalized = ((minuteOfDay % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{normalized / 60:00}:{normalized % 60:00}");
    }

    public static string Format(float timeOfDay) => FormatMinute(GetDisplayedMinute(timeOfDay));
}
