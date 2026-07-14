namespace SurvivalcraftTravelMap.Map;

public interface ITerrainMapSource
{
    bool IsColumnReady(int x, int z);

    bool IsChunkSurfaceReady(TerrainChunkCoordinate chunk) =>
        IsColumnReady(chunk.OriginX, chunk.OriginZ);

    int GetTopHeight(int x, int z);

    int GetContent(int x, int y, int z);

    int GetSeasonalTemperature(int x, int z);

    int GetSeasonalHumidity(int x, int z);
}

public sealed class TerrainMapSampler
{
    private readonly ITerrainMapSource _terrain;
    private readonly IReadOnlyDictionary<int, BlockPixelData> _blockPixels;

    public TerrainMapSampler(ITerrainMapSource terrain, Stream blockPixelJson)
        : this(terrain, BlockPixelData.LoadDictionary(blockPixelJson))
    {
    }

    public TerrainMapSampler(
        ITerrainMapSource terrain,
        IReadOnlyDictionary<int, BlockPixelData> blockPixels)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        _terrain = terrain;
        _blockPixels = BlockPixelData.ValidateAndCopy(blockPixels);
    }

    public Rgba32 Sample(int x, int z)
    {
        var topHeight = _terrain.GetTopHeight(x, z);
        var content = _terrain.GetContent(x, topHeight, z);
        if (content is 28 or 99 or 19 or 174 or 25)
        {
            content = _terrain.GetContent(x, topHeight - 1, z);
        }

        if (content is 226 or 229 or 232 or 233)
        {
            content = 18;
        }

        if (!_blockPixels.TryGetValue(content, out var pixel))
        {
            if (content < 0)
            {
                throw new InvalidDataException($"Terrain content {content} is invalid.");
            }

            // Network/modded builds can register block IDs above the vanilla palette.
            // A missing optional palette entry must not make the whole 16x16 terrain
            // chunk disappear, so give every future positive ID a stable opaque color.
            return CreateGeneratedColor(content);
        }

        if (!pixel.NeedChangeWithEnvironment)
        {
            return pixel.Color;
        }

        var temperature = _terrain.GetSeasonalTemperature(x, z)
            + GetTemperatureAdjustmentAtHeight(topHeight);
        var humidity = _terrain.GetSeasonalHumidity(x, z);
        var environmentColor = EnvironmentPalettes.Lookup(content, temperature, humidity);
        return Multiply(environmentColor, pixel.Color);
    }

    public bool TrySample(int x, int z, out Rgba32 color)
    {
        if (!_terrain.IsColumnReady(x, z))
        {
            color = default;
            return false;
        }

        color = Sample(x, z);
        if (color.A != 0)
        {
            return true;
        }

        color = default;
        return false;
    }

    public bool TrySampleChunk(
        TerrainChunkCoordinate chunk,
        Span<Rgba32> destination)
    {
        if (destination.Length != TerrainChunkCoordinate.PixelCount)
        {
            throw new ArgumentException(
                $"Destination length must be exactly {TerrainChunkCoordinate.PixelCount} pixels.",
                nameof(destination));
        }

        if (!_terrain.IsChunkSurfaceReady(chunk))
        {
            return false;
        }

        for (var localZ = 0; localZ < TerrainChunkCoordinate.Size; localZ++)
        {
            for (var localX = 0; localX < TerrainChunkCoordinate.Size; localX++)
            {
                var color = Sample(chunk.OriginX + localX, chunk.OriginZ + localZ);
                destination[(localZ * TerrainChunkCoordinate.Size) + localX] = color;
                if (color.A == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int GetTemperatureAdjustmentAtHeight(int y) =>
        (int)Math.Round(y > 64
            ? -0.0008f * (y - 64) * (y - 64)
            : 0.1f * (64 - y));

    private static Rgba32 Multiply(Rgba32 left, Rgba32 right) => new(
        (byte)(left.R * right.R / 255),
        (byte)(left.G * right.G / 255),
        (byte)(left.B * right.B / 255),
        (byte)(left.A * right.A / 255));

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

        if (sector < 1.0)
        {
            red = chroma;
            green = x;
        }
        else if (sector < 2.0)
        {
            red = x;
            green = chroma;
        }
        else if (sector < 3.0)
        {
            green = chroma;
            blue = x;
        }
        else if (sector < 4.0)
        {
            green = x;
            blue = chroma;
        }
        else if (sector < 5.0)
        {
            red = x;
            blue = chroma;
        }
        else
        {
            red = chroma;
            blue = x;
        }

        var match = value - chroma;
        return new Rgba32(
            (byte)Math.Round((red + match) * byte.MaxValue),
            (byte)Math.Round((green + match) * byte.MaxValue),
            (byte)Math.Round((blue + match) * byte.MaxValue),
            byte.MaxValue);
    }

    private static class EnvironmentPalettes
    {
        private static readonly IReadOnlyDictionary<int, Palette> ByContent = new Dictionary<int, Palette>
        {
            [8] = new((151, 184, 195), (210, 201, 93), (151, 184, 195), (79, 225, 56)),
            [12] = new((96, 161, 123), (174, 164, 42), (96, 161, 123), (30, 191, 1)),
            [13] = new((76, 181, 96), (174, 109, 42), (66, 215, 116), (77, 235, 96)),
            [14] = new((96, 161, 155), (129, 174, 42), (96, 161, 155), (1, 191, 53)),
            [18] = new((0, 0, 120), (0, 80, 100), (0, 40, 85), (0, 113, 97)),
            [225] = new((90, 141, 165), (119, 152, 51), (86, 141, 165), (1, 158, 65)),
            [256] = new((146, 191, 176), (160, 191, 176), (146, 191, 166), (150, 201, 141)),
        };

        internal static Rgba32 Lookup(int content, int temperature, int humidity)
        {
            if (!ByContent.TryGetValue(content, out var palette))
            {
                throw new InvalidDataException(
                    $"Block pixel color entry {content} requests environmental tinting but has no palette.");
            }

            return palette.Lookup(temperature, humidity);
        }

        private readonly record struct Palette(
            (byte R, byte G, byte B) ColdDry,
            (byte R, byte G, byte B) WarmDry,
            (byte R, byte G, byte B) ColdWet,
            (byte R, byte G, byte B) WarmWet)
        {
            internal Rgba32 Lookup(int temperature, int humidity)
            {
                var temperatureFactor = Math.Clamp(temperature, 0, 15) / 8f;
                temperatureFactor = Math.Min(temperatureFactor, 1f);
                var humidityFactor = Math.Clamp((humidity - 4) / 10f, 0f, 1f);
                var dry = Lerp(ColdDry, WarmDry, temperatureFactor);
                var wet = Lerp(ColdWet, WarmWet, temperatureFactor);
                var result = Lerp(dry, wet, humidityFactor);
                return new Rgba32(result.R, result.G, result.B, 255);
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
}
