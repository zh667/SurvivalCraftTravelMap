namespace SurvivalcraftTravelMap.Map;

public readonly record struct Rgba32(byte R, byte G, byte B, byte A);

public readonly record struct MapTerrainPixel(
    Rgba32 Color,
    byte HeightShade,
    MapSeasonSample Season = default);

public sealed class MapTile
{
    public const int Size = 64;
    internal const int PixelCount = Size * Size;
    internal const int ExploredByteCount = PixelCount / 8;
    internal const int ColorByteCount = PixelCount * 4;
    internal const int HeightShadeByteCount = PixelCount;
    internal const int SeasonByteCount = PixelCount;

    private readonly byte[] _explored;
    private readonly byte[] _colors;
    private readonly byte[] _heightShades;
    private readonly byte[] _seasons;
    private readonly object _sync = new();
    private long _version;

    public MapTile(int tileX, int tileZ)
        : this(
            tileX,
            tileZ,
            new byte[ExploredByteCount],
            new byte[ColorByteCount],
            new byte[HeightShadeByteCount],
            new byte[SeasonByteCount])
    {
    }

    internal MapTile(int tileX, int tileZ, byte[] explored, byte[] colors)
        : this(tileX, tileZ, explored, colors, new byte[HeightShadeByteCount])
    {
    }

    internal MapTile(int tileX, int tileZ, byte[] explored, byte[] colors, byte[] heightShades)
        : this(tileX, tileZ, explored, colors, heightShades, new byte[SeasonByteCount])
    {
    }

    internal MapTile(
        int tileX,
        int tileZ,
        byte[] explored,
        byte[] colors,
        byte[] heightShades,
        byte[] seasons)
    {
        ArgumentNullException.ThrowIfNull(explored);
        ArgumentNullException.ThrowIfNull(seasons);
        if (seasons.Length != SeasonByteCount)
        {
            throw new ArgumentException(
                $"Season data must be exactly {SeasonByteCount} bytes.",
                nameof(seasons));
        }

        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(heightShades);
        if (explored.Length != ExploredByteCount)
        {
            throw new ArgumentException($"Exploration data must be exactly {ExploredByteCount} bytes.", nameof(explored));
        }

        if (colors.Length != ColorByteCount)
        {
            throw new ArgumentException($"Color data must be exactly {ColorByteCount} bytes.", nameof(colors));
        }

        if (heightShades.Length != HeightShadeByteCount)
        {
            throw new ArgumentException(
                $"Height-shade data must be exactly {HeightShadeByteCount} bytes.",
                nameof(heightShades));
        }

        TileX = tileX;
        TileZ = tileZ;
        _explored = explored;
        _colors = colors;
        _heightShades = heightShades;
        _seasons = seasons;
    }

    public int TileX { get; }

    public int TileZ { get; }

    public long Version => Interlocked.Read(ref _version);

    public void SetPixel(int x, int z, Rgba32 color)
    {
        SetPixel(x, z, color, heightShade: 0);
    }

    public void SetPixel(int x, int z, Rgba32 color, byte heightShade)
    {
        var pixelIndex = GetPixelIndex(x, z);
        lock (_sync)
        {
            SetPixelCore(pixelIndex, color);
            _heightShades[pixelIndex] = color.A == 0 ? (byte)0 : heightShade;
            _seasons[pixelIndex] = 0;
            _version++;
        }
    }

    public void SetRegion(
        int x,
        int z,
        int width,
        int height,
        ReadOnlySpan<Rgba32> colors)
    {
        SetRegion(x, z, width, height, colors, ReadOnlySpan<byte>.Empty);
    }

    public void SetRegion(
        int x,
        int z,
        int width,
        int height,
        ReadOnlySpan<Rgba32> colors,
        ReadOnlySpan<byte> heightShades)
    {
        SetRegion(x, z, width, height, colors, heightShades, ReadOnlySpan<byte>.Empty);
    }

    /// <param name="seasons">
    /// One <see cref="MapSeasonCode"/> per pixel, or empty for pixels the seasons do not affect.
    /// </param>
    public void SetRegion(
        int x,
        int z,
        int width,
        int height,
        ReadOnlySpan<Rgba32> colors,
        ReadOnlySpan<byte> heightShades,
        ReadOnlySpan<byte> seasons)
    {
        if ((uint)x >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)z >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var pixelCount = checked(width * height);
        var endX = checked(x + width);
        var endZ = checked(z + height);
        if (endX > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (endZ > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (colors.Length != pixelCount)
        {
            throw new ArgumentException(
                $"Region colors must contain exactly {pixelCount} pixels.",
                nameof(colors));
        }

        if (!heightShades.IsEmpty && heightShades.Length != pixelCount)
        {
            throw new ArgumentException(
                $"Region height shades must contain exactly {pixelCount} values.",
                nameof(heightShades));
        }

        if (!seasons.IsEmpty && seasons.Length != pixelCount)
        {
            throw new ArgumentException(
                $"Region seasons must contain exactly {pixelCount} values.",
                nameof(seasons));
        }

        lock (_sync)
        {
            for (var localZ = 0; localZ < height; localZ++)
            {
                var tileRowStart = ((z + localZ) * Size) + x;
                var sourceRowStart = localZ * width;
                for (var localX = 0; localX < width; localX++)
                {
                    var pixelIndex = tileRowStart + localX;
                    var color = colors[sourceRowStart + localX];
                    SetPixelCore(pixelIndex, color);
                    _heightShades[pixelIndex] = color.A == 0 || heightShades.IsEmpty
                        ? (byte)0
                        : heightShades[sourceRowStart + localX];
                    _seasons[pixelIndex] = color.A == 0 || seasons.IsEmpty
                        ? (byte)0
                        : seasons[sourceRowStart + localX];
                }
            }

            _version++;
        }
    }

    public bool TryGetPixel(int x, int z, out Rgba32 color)
    {
        var pixelIndex = GetPixelIndex(x, z);
        lock (_sync)
        {
            return TryGetPixelCore(_explored, _colors, pixelIndex, out color);
        }
    }

    public bool TryGetTerrainPixel(int x, int z, out MapTerrainPixel pixel)
    {
        var pixelIndex = GetPixelIndex(x, z);
        lock (_sync)
        {
            return TryGetTerrainPixelCore(
                _explored,
                _colors,
                _heightShades,
                _seasons,
                pixelIndex,
                out pixel);
        }
    }

    public bool IsRegionFullyExplored(int x, int z, int width, int height)
    {
        ValidateRegion(x, z, width, height);

        lock (_sync)
        {
            for (var localZ = 0; localZ < height; localZ++)
            {
                var rowStart = ((z + localZ) * Size) + x;
                for (var localX = 0; localX < width; localX++)
                {
                    if (!TryGetPixelCore(_explored, _colors, rowStart + localX, out _))
                        return false;
                }
            }

            return true;
        }
    }

    public bool IsRegionFullyHeightShaded(int x, int z, int width, int height)
    {
        ValidateRegion(x, z, width, height);

        lock (_sync)
        {
            for (var localZ = 0; localZ < height; localZ++)
            {
                var rowStart = ((z + localZ) * Size) + x;
                for (var localX = 0; localX < width; localX++)
                {
                    var pixelIndex = rowStart + localX;
                    if (!TryGetPixelCore(_explored, _colors, pixelIndex, out _)
                        || _heightShades[pixelIndex] == TerrainHeightShading.Unknown)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>True when every explored pixel of the region was recorded with season data.</summary>
    public bool IsRegionFullySeasonTracked(int x, int z, int width, int height)
    {
        ValidateRegion(x, z, width, height);

        lock (_sync)
        {
            for (var localZ = 0; localZ < height; localZ++)
            {
                var rowStart = ((z + localZ) * Size) + x;
                for (var localX = 0; localX < width; localX++)
                {
                    if (_seasons[rowStart + localX] == 0
                        && TryGetPixelCore(_explored, _colors, rowStart + localX, out _))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    internal static void ValidateRegion(int x, int z, int width, int height)
    {
        if ((uint)x >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)z >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        if (width <= 0 || width > Size - x)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0 || height > Size - z)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }
    }

    public VersionedMapTileSnapshot CreateVersionedSnapshot()
    {
        lock (_sync)
        {
            return new VersionedMapTileSnapshot(
                _version,
                new MapTileSnapshot(
                    TileX,
                    TileZ,
                    (byte[])_explored.Clone(),
                    (byte[])_colors.Clone(),
                    (byte[])_heightShades.Clone(),
                    (byte[])_seasons.Clone()));
        }
    }

    internal void CopyExploredTo(Span<byte> destination)
    {
        lock (_sync)
        {
            _explored.CopyTo(destination);
        }
    }

    internal void CopyColorsTo(Span<byte> destination)
    {
        lock (_sync)
        {
            _colors.CopyTo(destination);
        }
    }

    internal void CopyHeightShadesTo(Span<byte> destination)
    {
        lock (_sync)
        {
            _heightShades.CopyTo(destination);
        }
    }

    internal void CopySeasonsTo(Span<byte> destination)
    {
        lock (_sync)
        {
            _seasons.CopyTo(destination);
        }
    }

    internal MapTile CreateSnapshot()
    {
        lock (_sync)
        {
            return new MapTile(
                TileX,
                TileZ,
                (byte[])_explored.Clone(),
                (byte[])_colors.Clone(),
                (byte[])_heightShades.Clone(),
                (byte[])_seasons.Clone());
        }
    }

    internal static bool TryGetPixelCore(
        byte[] explored,
        byte[] colors,
        int pixelIndex,
        out Rgba32 color)
    {
        if ((explored[pixelIndex >> 3] & (1 << (pixelIndex & 7))) == 0)
        {
            color = default;
            return false;
        }

        var colorIndex = pixelIndex * 4;
        if (colors[colorIndex + 3] == 0)
        {
            color = default;
            return false;
        }

        color = new Rgba32(
            colors[colorIndex],
            colors[colorIndex + 1],
            colors[colorIndex + 2],
            colors[colorIndex + 3]);
        return true;
    }

    internal static bool TryGetTerrainPixelCore(
        byte[] explored,
        byte[] colors,
        byte[] heightShades,
        byte[] seasons,
        int pixelIndex,
        out MapTerrainPixel pixel)
    {
        if (!TryGetPixelCore(explored, colors, pixelIndex, out var color))
        {
            pixel = default;
            return false;
        }

        pixel = new MapTerrainPixel(
            color,
            heightShades[pixelIndex],
            MapSeasonCode.ToSample(seasons[pixelIndex]));
        return true;
    }

    private void SetPixelCore(int pixelIndex, Rgba32 color)
    {
        var exploredMask = (byte)(1 << (pixelIndex & 7));
        if (color.A == 0)
        {
            _explored[pixelIndex >> 3] &= (byte)~exploredMask;
        }
        else
        {
            _explored[pixelIndex >> 3] |= exploredMask;
        }

        var colorIndex = pixelIndex * 4;
        _colors[colorIndex] = color.R;
        _colors[colorIndex + 1] = color.G;
        _colors[colorIndex + 2] = color.B;
        _colors[colorIndex + 3] = color.A;
    }

    private static int GetPixelIndex(int x, int z)
    {
        if ((uint)x >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)z >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        return (z * Size) + x;
    }
}

public sealed class MapTileSnapshot
{
    private readonly byte[] _explored;
    private readonly byte[] _colors;
    private readonly byte[] _heightShades;
    private readonly byte[] _seasons;
    private readonly Lazy<RegionSums> _regionSums;

    internal MapTileSnapshot(
        int tileX,
        int tileZ,
        byte[] explored,
        byte[] colors,
        byte[] heightShades)
        : this(tileX, tileZ, explored, colors, heightShades, new byte[MapTile.SeasonByteCount])
    {
    }

    internal MapTileSnapshot(
        int tileX,
        int tileZ,
        byte[] explored,
        byte[] colors,
        byte[] heightShades,
        byte[] seasons)
    {
        TileX = tileX;
        TileZ = tileZ;
        _explored = explored;
        _colors = colors;
        _heightShades = heightShades;
        _seasons = seasons;
        _regionSums = new Lazy<RegionSums>(CreateRegionSums, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int TileX { get; }

    public int TileZ { get; }

    public bool TryGetPixel(int x, int z, out Rgba32 color)
    {
        if ((uint)x >= MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)z >= MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        return MapTile.TryGetPixelCore(_explored, _colors, (z * MapTile.Size) + x, out color);
    }

    public bool TryGetTerrainPixel(int x, int z, out MapTerrainPixel pixel)
    {
        if ((uint)x >= MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)z >= MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        return MapTile.TryGetTerrainPixelCore(
            _explored,
            _colors,
            _heightShades,
            _seasons,
            (z * MapTile.Size) + x,
            out pixel);
    }

    public bool TryGetExploredRegion(int x, int z, int width, int height, out Rgba32 color)
    {
        var found = TryGetExploredTerrainRegion(x, z, width, height, out var pixel);
        color = pixel.Color;
        return found;
    }

    public bool TryGetExploredTerrainRegion(
        int x,
        int z,
        int width,
        int height,
        out MapTerrainPixel pixel)
    {
        if ((uint)x >= MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)z >= MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var endX = checked(x + width);
        var endZ = checked(z + height);
        if (endX > MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (endZ > MapTile.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (width == 1 && height == 1)
        {
            return TryGetTerrainPixel(x, z, out pixel);
        }

        var sums = _regionSums.Value;
        var exploredCount = RegionSum(sums.Explored, x, z, endX, endZ);
        if (exploredCount == 0)
        {
            pixel = default;
            return false;
        }

        var shadeCount = RegionSum(sums.HeightShadeCount, x, z, endX, endZ);
        pixel = new MapTerrainPixel(
            new Rgba32(
                Average(RegionSum(sums.Red, x, z, endX, endZ), exploredCount),
                Average(RegionSum(sums.Green, x, z, endX, endZ), exploredCount),
                Average(RegionSum(sums.Blue, x, z, endX, endZ), exploredCount),
                Average(RegionSum(sums.Alpha, x, z, endX, endZ), exploredCount)),
            shadeCount == 0
                ? (byte)0
                : Average(RegionSum(sums.HeightShade, x, z, endX, endZ), shadeCount),
            sums.HasSeasons ? AverageSeason(sums, x, z, endX, endZ, exploredCount) : default);
        return true;
    }

    private RegionSums CreateRegionSums()
    {
        var sums = new RegionSums();
        for (var z = 0; z < MapTile.Size; z++)
        {
            for (var x = 0; x < MapTile.Size; x++)
            {
                var explored = MapTile.TryGetPixelCore(
                    _explored,
                    _colors,
                    (z * MapTile.Size) + x,
                    out var color);
                SetIntegralValue(sums.Explored, x, z, explored ? 1 : 0);
                SetIntegralValue(sums.Red, x, z, explored ? color.R : 0);
                SetIntegralValue(sums.Green, x, z, explored ? color.G : 0);
                SetIntegralValue(sums.Blue, x, z, explored ? color.B : 0);
                SetIntegralValue(sums.Alpha, x, z, explored ? color.A : 0);
                var heightShade = explored ? _heightShades[(z * MapTile.Size) + x] : (byte)0;
                SetIntegralValue(sums.HeightShade, x, z, heightShade);
                SetIntegralValue(sums.HeightShadeCount, x, z, heightShade == 0 ? 0 : 1);
            }
        }

        if (HasAnySeason())
        {
            sums.AllocateSeasons();
            for (var z = 0; z < MapTile.Size; z++)
            {
                for (var x = 0; x < MapTile.Size; x++)
                {
                    var index = (z * MapTile.Size) + x;
                    var code = MapTile.TryGetPixelCore(_explored, _colors, index, out _)
                        ? _seasons[index]
                        : (byte)0;
                    var kind = MapSeasonCode.GetKind(code);
                    var frozen = MapSeasonCode.WasObservedFrozen(code) ? 1 : 0;
                    var limit = kind == MapSeasonKind.None
                        ? 0
                        : MapSeasonCode.GetLimit(code) - MapSeasonCode.MinimumLimit;
                    var isLand = kind == MapSeasonKind.Land;
                    var isWater = kind == MapSeasonKind.Water;

                    // Counts never exceed 4096 per tile, so a count and its frozen sub-count share
                    // one integral (low and high 16 bits) without carrying into each other.
                    SetIntegralValue(sums.Land!, x, z, isLand ? 1 | (frozen << 16) : 0);
                    SetIntegralValue(sums.Water!, x, z, isWater ? 1 | (frozen << 16) : 0);
                    SetIntegralValue(sums.LandLimit!, x, z, isLand ? limit : 0);
                    SetIntegralValue(sums.WaterLimit!, x, z, isWater ? limit : 0);
                }
            }
        }

        return sums;
    }

    private bool HasAnySeason()
    {
        foreach (var code in _seasons)
        {
            if (code != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Region average of the season data. Each kind freezes as a whole at its mean limit, so when
    /// zoomed out a texel's snow line moves in steps of whole texels rather than blending.
    /// </summary>
    private static MapSeasonSample AverageSeason(
        RegionSums sums,
        int x,
        int z,
        int endX,
        int endZ,
        int exploredCount)
    {
        var landPacked = RegionSum(sums.Land!, x, z, endX, endZ);
        var waterPacked = RegionSum(sums.Water!, x, z, endX, endZ);
        var land = landPacked & 0xFFFF;
        var water = waterPacked & 0xFFFF;
        if (land == 0 && water == 0)
        {
            return default;
        }

        return new MapSeasonSample(
            Share(land, exploredCount),
            Share(landPacked >> 16, exploredCount),
            Share(water, exploredCount),
            Share(waterPacked >> 16, exploredCount),
            land == 0 ? (sbyte)0 : MeanLimit(RegionSum(sums.LandLimit!, x, z, endX, endZ), land),
            water == 0 ? (sbyte)0 : MeanLimit(RegionSum(sums.WaterLimit!, x, z, endX, endZ), water));
    }

    private static byte Share(int count, int total) =>
        (byte)(((count * byte.MaxValue) + (total / 2)) / total);

    private static sbyte MeanLimit(int offsetSum, int count) =>
        (sbyte)(Math.Round(offsetSum / (double)count, MidpointRounding.AwayFromZero)
            + MapSeasonCode.MinimumLimit);

    private static void SetIntegralValue(int[] integral, int x, int z, int value)
    {
        var stride = MapTile.Size + 1;
        var index = ((z + 1) * stride) + x + 1;
        integral[index] = value
            + integral[index - 1]
            + integral[index - stride]
            - integral[index - stride - 1];
    }

    private static int RegionSum(int[] integral, int startX, int startZ, int endX, int endZ)
    {
        var stride = MapTile.Size + 1;
        return integral[(endZ * stride) + endX]
            - integral[(startZ * stride) + endX]
            - integral[(endZ * stride) + startX]
            + integral[(startZ * stride) + startX];
    }

    private static byte Average(int sum, int count) => checked((byte)((sum + (count / 2)) / count));

    private sealed class RegionSums
    {
        private const int ElementCount = (MapTile.Size + 1) * (MapTile.Size + 1);

        public int[] Explored { get; } = new int[ElementCount];
        public int[] Red { get; } = new int[ElementCount];
        public int[] Green { get; } = new int[ElementCount];
        public int[] Blue { get; } = new int[ElementCount];
        public int[] Alpha { get; } = new int[ElementCount];
        public int[] HeightShade { get; } = new int[ElementCount];
        public int[] HeightShadeCount { get; } = new int[ElementCount];
        // Season integrals exist only for tiles that carry season data (recorded since seasons
        // were tracked); Land/Water hold count | frozenCount << 16, limits are offset to >= 0.
        public int[]? Land { get; private set; }
        public int[]? Water { get; private set; }
        public int[]? LandLimit { get; private set; }
        public int[]? WaterLimit { get; private set; }

        public bool HasSeasons => Land is not null;

        public void AllocateSeasons()
        {
            Land = new int[ElementCount];
            Water = new int[ElementCount];
            LandLimit = new int[ElementCount];
            WaterLimit = new int[ElementCount];
        }
    }
}

public readonly record struct VersionedMapTileSnapshot(long Version, MapTileSnapshot Snapshot);
