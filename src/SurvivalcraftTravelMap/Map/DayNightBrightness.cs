namespace SurvivalcraftTravelMap.Map;

public static class DayNightBrightness
{
    private const float DawnStart = 0.2f;
    private const float DawnEnd = 0.3f;
    private const float DuskStart = 0.7f;
    private const float DuskEnd = 0.8f;

    public static float Calculate(float timeOfDay, float minimum)
    {
        var time = timeOfDay - MathF.Floor(timeOfDay);
        var daylight = time switch
        {
            < DawnStart => 0f,
            < DawnEnd => SmoothStep(DawnStart, DawnEnd, time),
            <= DuskStart => 1f,
            < DuskEnd => 1f - SmoothStep(DuskStart, DuskEnd, time),
            _ => 0f,
        };

        return minimum + ((1f - minimum) * daylight);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var amount = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return amount * amount * (3f - (2f * amount));
    }
}
