namespace SurvivalcraftTravelMap.Map;

internal static class LegacyTerrainPaletteMigration
{
    private const string ReferencePaletteBase64 =
        "AAAAAGFhYf+Eajr/gICA/7mrgP98fHz/g3t6/9PElf+rq6v/xppT/8aaU//GmlP/oKCg/6CgoP94eHj//////5KSkv94XDL/qKio/6urq/+0AAD/h2U3/yVLCf/+/v7/cD/2//////+Pj4//h2U3/0o1F//+/v7/mpqa/+3Zlf+ampr/mpqa/5qamv+ampr/Q0ND//////8hISH/iIiI/yIiIv+JiYn//v7+/yVLCf//////QCoS/9HR0f+MQCH/fHx8/4dlN/+Pj4//uauA/7mrgP98fHz/j4+P/4dlN/+HZTf/paWlpXZ2dv9ePRr//////+709P/Y4vP/bkYe/3x8fP98fHz/iYmJ/3R0dP/WxrH/1sax/9bGsf8yyqb/n52V/4c9M/9AIxL/hz0z/4c9M/8lSwn/JUsJ/yVLCf/+/v7//v7+//7+/v92VzD/hoaG/+709P+HZTf/mpqa/7FVUP+xVVD/39/f/9/f3//peh3/39/f/4dlN/90dHT/dHR0/4dlN/+HZTf/ZFI8/8G0jv90dHT//Pz8/yVLCf//MjL/h2U3/4dlN/+HZTf//v7+/2BiO//f39///wAA/3R0dP//////JUsJ/3BKJP/+/v7/Xj0a//z8/P88aQD/39/f/9/f3//+/v7//v7+//7+/v/+/v7/WKf//1x0NP/f39//39/f/+rq6v++eS3/vnkt/5xWNf9DQ0P/Q0ND/4CAgP9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/ppQA/3R0dP8lSwn/DAwM/0NDQ/9DQ0P/h2U3/4CAgP9DQ0P/Q0ND/0NDQ//k4+D/5OPg/9/f3/+vz8r/r8/K/3R0dP+HPTP//////4dlN//f39//f2U2/yVLCf+ampr//////yVLCf/+/v7/EUEW/+rq6v8lSwn/k2g3/4dlN/9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/gICA/4CAgP/+/v7//v7+/8DAwP/AwMD/JUsJ/yIiIv8RcCr//v7+///////+/v7/JUsJ/4CAgP/+/v7///////7+/v/k4+D/LB8J/9LOw/8MDAz/39/f/9/f3//+/v7/39/f/2dnZ/8lSwn/QCoS/4CAgP8hISH///////7+/v/+/v7/Q0ND/4c9M/9DQ0P/eHh4/7CwsP+HZTf/JUsJ/7CwsP//////Q0ND/7CwsP+wsLD/h2U3/4dlN/+HZTf/Q0ND/3BKJP8lSwn/sVVQ/6/Pyv+TaDf/fHx8/21kQv/f39///Pz8/yVLCf+ampr/JUsJ/yVLCf/f39//39/f/0NDQ/9DQ0P/xppT/3h4eP8=";

    private static readonly byte[] ReferencePalette = Convert.FromBase64String(ReferencePaletteBase64);
    private static readonly Lazy<IReadOnlyDictionary<uint, Rgba32>> ColorMap = new(CreateColorMap);

    private static readonly IReadOnlyDictionary<int, Rgba32> LegacyOverrides =
        new Dictionary<int, Rgba32>
        {
            [0] = new(0, 0, 0, 0),
            [1] = new(112, 106, 99, 255),
            [2] = new(126, 91, 58, 255),
            [3] = new(132, 126, 121, 255),
            [7] = new(215, 195, 139, 255),
            [8] = new(255, 255, 255, 255),
            [9] = new(131, 91, 52, 255),
            [12] = new(255, 255, 255, 255),
            [13] = new(255, 255, 255, 255),
            [14] = new(255, 255, 255, 255),
            [16] = new(58, 58, 55, 255),
            [18] = new(255, 255, 255, 255),
            [39] = new(142, 124, 103, 255),
            [41] = new(169, 102, 65, 255),
            [61] = new(238, 242, 244, 255),
            [62] = new(168, 217, 228, 255),
            [66] = new(205, 198, 166, 255),
            [67] = new(55, 62, 64, 255),
            [92] = new(238, 85, 30, 255),
            [104] = new(244, 143, 35, 255),
            [112] = new(74, 191, 196, 255),
            [127] = new(52, 122, 65, 255),
            [225] = new(255, 255, 255, 255),
            [226] = new(47, 112, 154, 255),
            [229] = new(47, 112, 154, 255),
            [232] = new(47, 112, 154, 255),
            [233] = new(47, 112, 154, 255),
            [256] = new(255, 255, 255, 255),
        };

    private static readonly IReadOnlyDictionary<int, EnvironmentPalette> EnvironmentPalettes =
        new Dictionary<int, EnvironmentPalette>
        {
            [8] = new((151, 184, 195), (210, 201, 93), (151, 184, 195), (79, 225, 56)),
            [12] = new((96, 161, 123), (174, 164, 42), (96, 161, 123), (30, 191, 1)),
            [13] = new((76, 181, 96), (174, 109, 42), (66, 215, 116), (77, 235, 96)),
            [14] = new((96, 161, 155), (129, 174, 42), (96, 161, 155), (1, 191, 53)),
            [18] = new((0, 0, 120), (0, 80, 100), (0, 40, 85), (0, 113, 97)),
            [225] = new((90, 141, 165), (119, 152, 51), (86, 141, 165), (1, 158, 65)),
            [256] = new((146, 191, 176), (160, 191, 176), (146, 191, 166), (150, 201, 141)),
        };

    internal static void UpgradeExploredColors(ReadOnlySpan<byte> explored, Span<byte> colors)
    {
        if (explored.Length != MapTile.ExploredByteCount)
        {
            throw new ArgumentException("Exploration data has an invalid length.", nameof(explored));
        }

        if (colors.Length != MapTile.ColorByteCount)
        {
            throw new ArgumentException("Color data has an invalid length.", nameof(colors));
        }

        for (var pixelIndex = 0; pixelIndex < MapTile.PixelCount; pixelIndex++)
        {
            if ((explored[pixelIndex >> 3] & (1 << (pixelIndex & 7))) == 0)
            {
                continue;
            }

            var offset = pixelIndex * 4;
            var packed = Pack(colors[offset], colors[offset + 1], colors[offset + 2], colors[offset + 3]);
            if (!ColorMap.Value.TryGetValue(packed, out var replacement))
            {
                continue;
            }

            colors[offset] = replacement.R;
            colors[offset + 1] = replacement.G;
            colors[offset + 2] = replacement.B;
            colors[offset + 3] = replacement.A;
        }
    }

    internal static Rgba32 GetReferenceColor(int content)
    {
        if ((uint)content > BlockPixelData.MaximumBlockIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        var offset = content * 4;
        return new Rgba32(
            ReferencePalette[offset],
            ReferencePalette[offset + 1],
            ReferencePalette[offset + 2],
            ReferencePalette[offset + 3]);
    }

    private static IReadOnlyDictionary<uint, Rgba32> CreateColorMap()
    {
        var result = new Dictionary<uint, Rgba32>();
        for (var content = 1; content <= BlockPixelData.MaximumBlockIndex; content++)
        {
            result[Pack(GetLegacyColor(content))] = GetReferenceColor(content);
        }

        foreach (var (content, palette) in EnvironmentPalettes)
        {
            var referenceBase = GetReferenceColor(content);
            for (var temperature = 0; temperature <= 8; temperature++)
            {
                for (var humidity = 4; humidity <= 14; humidity++)
                {
                    var legacyColor = palette.Lookup(temperature, humidity);
                    result[Pack(legacyColor)] = Multiply(legacyColor, referenceBase);
                }
            }
        }

        return result;
    }

    private static Rgba32 GetLegacyColor(int content) =>
        LegacyOverrides.TryGetValue(content, out var color) ? color : CreateGeneratedColor(content);

    private static Rgba32 CreateGeneratedColor(int content)
    {
        var hue = (content * 47L) % 360L;
        var saturation = 0.24 + (((content * 13L) % 5L) * 0.035);
        var value = 0.44 + (((content * 7L) % 6L) * 0.035);
        var chroma = value * saturation;
        var sector = hue / 60.0;
        var x = chroma * (1.0 - Math.Abs((sector % 2.0) - 1.0));
        var red = 0.0;
        var green = 0.0;
        var blue = 0.0;

        if (sector < 1.0) { red = chroma; green = x; }
        else if (sector < 2.0) { red = x; green = chroma; }
        else if (sector < 3.0) { green = chroma; blue = x; }
        else if (sector < 4.0) { green = x; blue = chroma; }
        else if (sector < 5.0) { red = x; blue = chroma; }
        else { red = chroma; blue = x; }

        var match = value - chroma;
        return new Rgba32(
            (byte)Math.Round((red + match) * byte.MaxValue),
            (byte)Math.Round((green + match) * byte.MaxValue),
            (byte)Math.Round((blue + match) * byte.MaxValue),
            byte.MaxValue);
    }

    private static Rgba32 Multiply(Rgba32 left, Rgba32 right) => new(
        (byte)(left.R * right.R / 255),
        (byte)(left.G * right.G / 255),
        (byte)(left.B * right.B / 255),
        (byte)(left.A * right.A / 255));

    private static uint Pack(Rgba32 color) => Pack(color.R, color.G, color.B, color.A);

    private static uint Pack(byte red, byte green, byte blue, byte alpha) =>
        red | ((uint)green << 8) | ((uint)blue << 16) | ((uint)alpha << 24);

    private readonly record struct EnvironmentPalette(
        (byte R, byte G, byte B) ColdDry,
        (byte R, byte G, byte B) WarmDry,
        (byte R, byte G, byte B) ColdWet,
        (byte R, byte G, byte B) WarmWet)
    {
        internal Rgba32 Lookup(int temperature, int humidity)
        {
            var temperatureFactor = Math.Min(Math.Clamp(temperature, 0, 15) / 8f, 1f);
            var humidityFactor = Math.Clamp((humidity - 4) / 10f, 0f, 1f);
            var dry = Lerp(ColdDry, WarmDry, temperatureFactor);
            var wet = Lerp(ColdWet, WarmWet, temperatureFactor);
            var result = Lerp(dry, wet, humidityFactor);
            return new Rgba32(result.R, result.G, result.B, byte.MaxValue);
        }

        private static (byte R, byte G, byte B) Lerp(
            (byte R, byte G, byte B) from,
            (byte R, byte G, byte B) to,
            float amount) =>
            (
                (byte)(from.R + ((to.R - from.R) * amount)),
                (byte)(from.G + ((to.G - from.G) * amount)),
                (byte)(from.B + ((to.B - from.B) * amount))
            );
    }
}
