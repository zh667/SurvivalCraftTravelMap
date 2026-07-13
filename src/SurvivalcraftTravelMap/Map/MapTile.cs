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

    public void SetPixel(int x, int z, Rgba32 color)
    {
        var pixelIndex = GetPixelIndex(x, z);
        _explored[pixelIndex >> 3] |= (byte)(1 << (pixelIndex & 7));

        var colorIndex = pixelIndex * 4;
        _colors[colorIndex] = color.R;
        _colors[colorIndex + 1] = color.G;
        _colors[colorIndex + 2] = color.B;
        _colors[colorIndex + 3] = color.A;
    }

    public bool TryGetPixel(int x, int z, out Rgba32 color)
    {
        var pixelIndex = GetPixelIndex(x, z);
        if ((_explored[pixelIndex >> 3] & (1 << (pixelIndex & 7))) == 0)
        {
            color = default;
            return false;
        }

        var colorIndex = pixelIndex * 4;
        color = new Rgba32(
            _colors[colorIndex],
            _colors[colorIndex + 1],
            _colors[colorIndex + 2],
            _colors[colorIndex + 3]);
        return true;
    }

    internal void CopyExploredTo(Span<byte> destination) => _explored.CopyTo(destination);

    internal void CopyColorsTo(Span<byte> destination) => _colors.CopyTo(destination);

    internal MapTile CreateSnapshot() => new(TileX, TileZ, (byte[])_explored.Clone(), (byte[])_colors.Clone());

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
