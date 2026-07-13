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

    internal MapTileSnapshot(int tileX, int tileZ, byte[] explored, byte[] colors)
    {
        TileX = tileX;
        TileZ = tileZ;
        _explored = explored;
        _colors = colors;
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
}

public readonly record struct VersionedMapTileSnapshot(long Version, MapTileSnapshot Snapshot);
