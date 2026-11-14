namespace SurvivalcraftTravelMap.Map;

public interface ITerrainMapSource
{
    bool IsColumnReady(int x, int z);

    bool IsChunkSurfaceReady(TerrainChunkCoordinate chunk) =>
        IsColumnReady(chunk.OriginX, chunk.OriginZ);

    int GetTopHeight(int x, int z);

    int GetContent(int x, int y, int z);

    bool IsPassableCell(int x, int y, int z) => GetContent(x, y, z) == 0;

    bool IsCollidableCell(int x, int y, int z) => !IsPassableCell(x, y, z);

    bool IsFluidCell(int x, int y, int z) => false;

    int GetSeasonalTemperature(int x, int z);

    int GetSeasonalHumidity(int x, int z);

    bool IsCrossPlant(int content) => false;

    bool TryGetSolidHeight(int x, int z, out int height)
    {
        height = 0;
        return false;
    }

    bool TryGetEnvironmentColor(
        int content,
        int temperature,
        int humidity,
        out Rgba32 color)
    {
        color = default;
        return false;
    }

    /// <summary>
    /// Whether the game would treat this place as frozen at the given season temperature offset:
    /// <c>SubsystemWeather.IsPlaceFrozen(seasonal temperature, y)</c>, with the seasonal
    /// temperature computed the way the edition does it (the plugin edition clamps it at 0).
    /// </summary>
    bool IsPlaceFrozenAtSeason(int x, int z, int y, int seasonTemperature) =>
        GetSeasonalTemperature(x, z) + seasonTemperature
            + TerrainMapSampler.GetTemperatureAdjustmentAtHeight(y) <= 0;

    /// <summary>
    /// Whether snow settles on (or replaces) the top cell when the place freezes — the game's
    /// <c>SubsystemSnowBlockBehavior.CanSupportSnow</c> / <c>CanBeReplacedBySnow</c>.
    /// </summary>
    bool CanHoldSnow(int x, int y, int z) => false;

    /// <summary>
    /// The terrain generator's distance to the ocean shore (negative out at sea); the game only
    /// ices water where it is above -35. A source that cannot tell treats all water as open sea.
    /// </summary>
    float GetOceanShoreDistance(int x, int z) => float.NegativeInfinity;
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
        return SampleCore(x, z).Color;
    }

    public Rgba32 SampleContent(int content, int x, int y, int z)
    {
        if (content is 28 or 99 or 174 or 25)
        {
            content = y > 0 ? _terrain.GetContent(x, y - 1, z) : content;
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

            return CreateGeneratedColor(content);
        }

        if (!pixel.NeedChangeWithEnvironment)
        {
            return pixel.Color;
        }

        var temperature = _terrain.GetSeasonalTemperature(x, z)
            + GetTemperatureAdjustmentAtHeight(y);
        var humidity = _terrain.GetSeasonalHumidity(x, z);
        var environmentColor = _terrain.TryGetEnvironmentColor(
            content,
            temperature,
            humidity,
            out var gameColor)
            ? gameColor
            : EnvironmentPalettes.Lookup(content, temperature, humidity);
        return Multiply(environmentColor, pixel.Color);
    }

    internal const int SnowContent = 61;
    internal const int IceContent = 62;
    internal const int WaterContent = 18;

    private TerrainSample SampleCore(int x, int z)
    {
        var topHeight = _terrain.GetTopHeight(x, z);
        var content = _terrain.GetContent(x, topHeight, z);

        // The game tests IsPlaceFrozen at the column's top as it stands: the snow layer when there
        // is snow (deciding whether it melts), the bare surface when there is none (deciding whether
        // snow falls) — one block apart, which matters on mountains (seen in the isolated run). The
        // column stays as recorded while unloaded, so that is the height to judge it by.
        var freezeTestHeight = topHeight;

        // Record the bare surface and how the seasons change it, not the snow or ice lying on it:
        // the map then shows each season the way the game will have it when the player arrives.
        var seasonKind = MapSeasonKind.Fixed;
        var observedFrozen = false;
        if (content == SnowContent && topHeight > 0)
        {
            topHeight--;
            content = _terrain.GetContent(x, topHeight, z);
            seasonKind = MapSeasonKind.Land;
            observedFrozen = true;
        }
        else if (content == IceContent)
        {
            content = WaterContent;
            seasonKind = MapSeasonKind.Water;
            observedFrozen = true;
        }
        else if (content is WaterContent or 226 or 229 or 232 or 233)
        {
            if (CanIceOver(x, z))
            {
                seasonKind = MapSeasonKind.Water;
            }
        }
        else if (_terrain.CanHoldSnow(x, topHeight, z))
        {
            seasonKind = MapSeasonKind.Land;
        }

        var season = seasonKind == MapSeasonKind.Fixed
            ? MapSeasonCode.Create(MapSeasonKind.Fixed, false, 0)
            : MapSeasonCode.Create(
                seasonKind,
                observedFrozen,
                FindFreezeLimit(x, z, freezeTestHeight));
        var sample = SampleSurface(x, z, topHeight, content);
        return sample with { Season = season };
    }

    /// <summary>
    /// Water ices over when its place freezes unless it lies far out at sea. The game freezes a
    /// water cell next to anything solid, but it visits a chunk's columns in random order and new
    /// ice counts as solid, so in one load the ice runs from the shore across whole lakes and bays
    /// (measured in the isolated game run: a shore-only rule missed 759 of 6561 cells in winter).
    /// </summary>
    private bool CanIceOver(int x, int z) => _terrain.GetOceanShoreDistance(x, z) > -35f;

    /// <summary>
    /// The highest season temperature at which this place counts as frozen, found with the game's
    /// own rule (mods may replace it), assuming only that colder never thaws what warmer froze.
    /// </summary>
    private int FindFreezeLimit(int x, int z, int y)
    {
        var low = MapSeasonCode.MinimumLimit;
        var high = MapSeasonCode.MaximumLimit;
        if (!_terrain.IsPlaceFrozenAtSeason(x, z, y, low))
        {
            return low;
        }

        if (_terrain.IsPlaceFrozenAtSeason(x, z, y, high))
        {
            return high;
        }

        // Invariant: frozen at low, thawed at high.
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (_terrain.IsPlaceFrozenAtSeason(x, z, y, middle))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private TerrainSample SampleSurface(int x, int z, int topHeight, int content)
    {
        var isCrossPlant = _terrain.IsCrossPlant(content);
        if (content is 28 or 99 or 174 or 25)
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
            return new TerrainSample(
                CreateGeneratedColor(content),
                isCrossPlant ? Math.Max(0, topHeight - 1) : topHeight,
                isCrossPlant,
                0);
        }

        if (!pixel.NeedChangeWithEnvironment)
        {
            return new TerrainSample(
                pixel.Color,
                isCrossPlant ? Math.Max(0, topHeight - 1) : topHeight,
                isCrossPlant,
                0);
        }

        var temperature = _terrain.GetSeasonalTemperature(x, z)
            + GetTemperatureAdjustmentAtHeight(topHeight);
        var humidity = _terrain.GetSeasonalHumidity(x, z);
        var environmentColor = _terrain.TryGetEnvironmentColor(
            content,
            temperature,
            humidity,
            out var gameColor)
            ? gameColor
            : EnvironmentPalettes.Lookup(content, temperature, humidity);
        return new TerrainSample(
            Multiply(environmentColor, pixel.Color),
            isCrossPlant ? Math.Max(0, topHeight - 1) : topHeight,
            isCrossPlant,
            0);
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
        Span<byte> heightShades = stackalloc byte[TerrainChunkCoordinate.PixelCount];
        return TrySampleChunk(chunk, destination, heightShades);
    }

    public bool TrySampleChunk(
        TerrainChunkCoordinate chunk,
        Span<Rgba32> destination,
        Span<byte> heightShades)
    {
        Span<byte> seasons = stackalloc byte[TerrainChunkCoordinate.PixelCount];
        return TrySampleChunk(chunk, destination, heightShades, seasons);
    }

    /// <param name="seasons">Receives one <see cref="MapSeasonCode"/> per pixel.</param>
    public bool TrySampleChunk(
        TerrainChunkCoordinate chunk,
        Span<Rgba32> destination,
        Span<byte> heightShades,
        Span<byte> seasons)
    {
        if (seasons.Length != TerrainChunkCoordinate.PixelCount)
        {
            throw new ArgumentException(
                $"Season destination must be exactly {TerrainChunkCoordinate.PixelCount} values.",
                nameof(seasons));
        }

        if (destination.Length != TerrainChunkCoordinate.PixelCount)
        {
            throw new ArgumentException(
                $"Destination length must be exactly {TerrainChunkCoordinate.PixelCount} pixels.",
                nameof(destination));
        }

        if (heightShades.Length != TerrainChunkCoordinate.PixelCount)
        {
            throw new ArgumentException(
                $"Height-shade destination must be exactly {TerrainChunkCoordinate.PixelCount} values.",
                nameof(heightShades));
        }

        if (!_terrain.IsChunkSurfaceReady(chunk))
        {
            return false;
        }

        const int heightGridSize = TerrainChunkCoordinate.Size + 2;
        Span<int> heights = stackalloc int[heightGridSize * heightGridSize];
        Span<byte> crossPlants = stackalloc byte[TerrainChunkCoordinate.PixelCount];
        for (var localZ = 0; localZ < TerrainChunkCoordinate.Size; localZ++)
        {
            for (var localX = 0; localX < TerrainChunkCoordinate.Size; localX++)
            {
                var sample = SampleCore(chunk.OriginX + localX, chunk.OriginZ + localZ);
                var index = (localZ * TerrainChunkCoordinate.Size) + localX;
                destination[index] = sample.Color;
                seasons[index] = sample.Season;
                heights[((localZ + 1) * heightGridSize) + localX + 1] = sample.SolidHeight;
                crossPlants[index] = sample.IsCrossPlant ? (byte)1 : (byte)0;
                if (sample.Color.A == 0)
                {
                    return false;
                }
            }
        }

        for (var localZ = 0; localZ < TerrainChunkCoordinate.Size; localZ++)
        {
            var row = (localZ + 1) * heightGridSize;
            heights[row] = TryGetSolidHeight(chunk.OriginX - 1, chunk.OriginZ + localZ)
                ?? heights[row + 1];
            heights[row + heightGridSize - 1] = TryGetSolidHeight(
                chunk.OriginX + TerrainChunkCoordinate.Size,
                chunk.OriginZ + localZ)
                ?? heights[row + heightGridSize - 2];
        }

        for (var localX = 0; localX < TerrainChunkCoordinate.Size; localX++)
        {
            heights[localX + 1] = TryGetSolidHeight(chunk.OriginX + localX, chunk.OriginZ - 1)
                ?? heights[heightGridSize + localX + 1];
            var bottom = (heightGridSize - 1) * heightGridSize;
            heights[bottom + localX + 1] = TryGetSolidHeight(
                chunk.OriginX + localX,
                chunk.OriginZ + TerrainChunkCoordinate.Size)
                ?? heights[bottom - heightGridSize + localX + 1];
        }

        for (var localZ = 0; localZ < TerrainChunkCoordinate.Size; localZ++)
        {
            for (var localX = 0; localX < TerrainChunkCoordinate.Size; localX++)
            {
                var pixelIndex = (localZ * TerrainChunkCoordinate.Size) + localX;
                var centerIndex = ((localZ + 1) * heightGridSize) + localX + 1;
                heightShades[pixelIndex] = crossPlants[pixelIndex] != 0
                    ? TerrainHeightShading.Neutral
                    : TerrainHeightShading.Calculate(
                        heights[centerIndex - 1],
                        heights[centerIndex - heightGridSize],
                        heights[centerIndex + 1],
                        heights[centerIndex + heightGridSize],
                        heights[centerIndex]);
            }
        }

        return true;
    }

    private int? TryGetSolidHeight(int x, int z) =>
        _terrain.TryGetSolidHeight(x, z, out var height) ? height : null;

    internal static int GetTemperatureAdjustmentAtHeight(int y) =>
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

    private readonly record struct TerrainSample(
        Rgba32 Color,
        int SolidHeight,
        bool IsCrossPlant,
        byte Season);

    private static class EnvironmentPalettes
    {
        private static readonly IReadOnlyDictionary<int, Palette> ByContent = new Dictionary<int, Palette>
        {
            [8] = new((151, 184, 195), (210, 201, 93), (151, 184, 195), (79, 225, 56)),
            [12] = new((96, 161, 123), (174, 164, 42), (96, 161, 123), (30, 191, 1)),
            [13] = new((76, 181, 96), (174, 109, 42), (66, 215, 116), (77, 235, 96)),
            [14] = new((96, 161, 155), (129, 174, 42), (96, 161, 155), (1, 191, 53)),
            [18] = new((0, 0, 120), (0, 80, 100), (0, 40, 85), (0, 113, 97)),
            [19] = new((151, 184, 195), (210, 201, 93), (151, 184, 195), (79, 225, 56)),
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
