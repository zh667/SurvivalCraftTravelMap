namespace SurvivalcraftTravelMap.Map;

public readonly record struct Rgba32(byte R, byte G, byte B, byte A);

public readonly record struct MapTerrainPixel(Rgba32 Color, byte HeightShade);

public sealed class MapTile
{
    public const int Size = 64;
    internal const int PixelCount = Size * Size;
    internal const int ExploredByteCount = PixelCount / 8;
    internal const int ColorByteCount = PixelCount * 4;
    internal const int HeightShadeByteCount = PixelCount;

    private readonly byte[] _explored;
    private readonly byte[] _colors;
    private readonly byte[] _heightShades;
    private readonly object _sync = new();
    private long _version;

    public MapTile(int tileX, int tileZ)
        : this(
            tileX,
            tileZ,
            new byte[ExploredByteCount],
            new byte[ColorByteCount],
            new byte[HeightShadeByteCount])
    {
    }

    internal MapTile(int tileX, int tileZ, byte[] explored, byte[] colors)
        : this(tileX, tileZ, explored, colors, new byte[HeightShadeByteCount])
    {
    }

    internal MapTile(int tileX, int tileZ, byte[] explored, byte[] colors, byte[] heightShades)
    {
        ArgumentNullException.ThrowIfNull(explored);
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
                    (byte[])_heightShades.Clone()));
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

    internal MapTile CreateSnapshot()
    {
        lock (_sync)
        {
            return new MapTile(
                TileX,
                TileZ,
                (byte[])_explored.Clone(),
                (byte[])_colors.Clone(),
                (byte[])_heightShades.Clone());
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
        int pixelIndex,
        out MapTerrainPixel pixel)
    {
        if (!TryGetPixelCore(explored, colors, pixelIndex, out var color))
        {
            pixel = default;
            return false;
        }

        pixel = new MapTerrainPixel(color, heightShades[pixelIndex]);
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
    private readonly Lazy<RegionSums> _regionSums;

    internal MapTileSnapshot(
        int tileX,
        int tileZ,
        byte[] explored,
        byte[] colors,
        byte[] heightShades)
    {
        TileX = tileX;
        TileZ = tileZ;
        _explored = explored;
        _colors = colors;
        _heightShades = heightShades;
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
                : Average(RegionSum(sums.HeightShade, x, z, endX, endZ), shadeCount));
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

        return sums;
    }

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
    }
}

public readonly record struct VersionedMapTileSnapshot(long Version, MapTileSnapshot Snapshot);
