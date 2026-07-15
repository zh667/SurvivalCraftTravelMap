namespace SurvivalcraftTravelMap.Map;

public static class TerrainHeightShading
{
    public const byte Unknown = 0;
    public const byte Neutral = 128;

    public static byte Calculate(
        int westHeight,
        int northHeight,
        int eastHeight,
        int southHeight,
        int centerHeight)
    {
        var gradientX = (eastHeight - westHeight) * 0.5f;
        var gradientZ = (southHeight - northHeight) * 0.5f;
        var normalLength = MathF.Sqrt((gradientX * gradientX) + (gradientZ * gradientZ) + 4f);
        var light = MathF.Max(
            0f,
            ((gradientX + gradientZ + 2f) * 0.5773503f) / normalLength);
        var brightness = 0.52f + (light * 0.83f);

        if (westHeight > centerHeight
            && northHeight > centerHeight
            && eastHeight > centerHeight
            && southHeight > centerHeight)
        {
            var depth = Math.Max(
                Math.Max(westHeight, eastHeight),
                Math.Max(northHeight, southHeight)) - centerHeight;
            brightness -= MathF.Min(0.28f, depth * 0.035f);
        }

        if (centerHeight > westHeight
            && centerHeight > northHeight
            && centerHeight > eastHeight
            && centerHeight > southHeight)
        {
            var prominence = centerHeight - Math.Min(
                Math.Min(westHeight, eastHeight),
                Math.Min(northHeight, southHeight));
            brightness += MathF.Min(0.22f, prominence * 0.025f);
        }

        return Encode(Math.Clamp(brightness, 0.25f, 1.5f));
    }

    public static byte Encode(float brightness) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(brightness, 0.25f, 1.5f) * Neutral),
        1,
        byte.MaxValue));

    public static float Decode(byte encoded) => encoded == Unknown ? 1f : encoded / (float)Neutral;

    public static Rgba32 Apply(Rgba32 color, byte encoded, float brightness = 1f)
    {
        var factor = Decode(encoded) * Math.Clamp(float.IsFinite(brightness) ? brightness : 1f, 0f, 1f);
        return new Rgba32(
            Scale(color.R, factor),
            Scale(color.G, factor),
            Scale(color.B, factor),
            color.A);
    }

    private static byte Scale(byte component, float factor) =>
        (byte)Math.Clamp((int)MathF.Round(component * factor), 0, byte.MaxValue);
}
