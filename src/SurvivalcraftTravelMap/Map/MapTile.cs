namespace SurvivalcraftTravelMap.Map;

public readonly record struct Rgba32(byte R, byte G, byte B, byte A);

public sealed class MapTile
{
    public const int Size = 64;
    internal const int PixelCount = Size * Size;
    internal const int ExploredByteCount = PixelCount / 8;
    internal const int ColorByteCount = PixelCount * 4;

    private readonly byte[] _explored;
    private readonly byte[] _colors;
    private readonly object _sync = new();
    private long _version;

    public MapTile(int tileX, int tileZ)
        : this(tileX, tileZ, new byte[ExploredByteCount], new byte[ColorByteCount])
    {
    }

    internal MapTile(int tileX, int tileZ, byte[] explored, byte[] colors)
    {
        ArgumentNullException.ThrowIfNull(explored);
        ArgumentNullException.ThrowIfNull(colors);
        if (explored.Length != ExploredByteCount)
        {
            throw new ArgumentException($"Exploration data must be exactly {ExploredByteCount} bytes.", nameof(explored));
        }

        if (colors.Length != ColorByteCount)
        {
            throw new ArgumentException($"Color data must be exactly {ColorByteCount} bytes.", nameof(colors));
        }

        TileX = tileX;
        TileZ = tileZ;
        _explored = explored;
        _colors = colors;
    }

    public int TileX { get; }

    public int TileZ { get; }

    public long Version => Interlocked.Read(ref _version);

    public void SetPixel(int x, int z, Rgba32 color)
    {
        var pixelIndex = GetPixelIndex(x, z);
        lock (_sync)
        {
            SetPixelCore(pixelIndex, color);
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
                    (byte[])_colors.Clone()));
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

    internal MapTile CreateSnapshot()
    {
        lock (_sync)
        {
            return new MapTile(TileX, TileZ, (byte[])_explored.Clone(), (byte[])_colors.Clone());
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
    private readonly Lazy<RegionSums> _regionSums;

    internal MapTileSnapshot(int tileX, int tileZ, byte[] explored, byte[] colors)
    {
        TileX = tileX;
        TileZ = tileZ;
        _explored = explored;
        _colors = colors;
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

    public bool TryGetExploredRegion(int x, int z, int width, int height, out Rgba32 color)
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
            return TryGetPixel(x, z, out color);
        }

        var sums = _regionSums.Value;
        var pixelCount = checked(width * height);
        if (RegionSum(sums.Explored, x, z, endX, endZ) != pixelCount)
        {
            color = default;
            return false;
        }

        color = new Rgba32(
            Average(RegionSum(sums.Red, x, z, endX, endZ), pixelCount),
            Average(RegionSum(sums.Green, x, z, endX, endZ), pixelCount),
            Average(RegionSum(sums.Blue, x, z, endX, endZ), pixelCount),
            Average(RegionSum(sums.Alpha, x, z, endX, endZ), pixelCount));
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
    }
}

public readonly record struct VersionedMapTileSnapshot(long Version, MapTileSnapshot Snapshot);
