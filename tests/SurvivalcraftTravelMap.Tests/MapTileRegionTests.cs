using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MapTileRegionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(16, 32)]
    [InlineData(48, 48)]
    public void SetRegion_writes_all_pixels_and_explored_bits_with_one_version_change(int x, int z)
    {
        const int width = 16;
        const int height = 16;
        var tile = new MapTile(2, -3);
        var outsideColor = new Rgba32(201, 202, 203, 204);
        tile.SetPixel(63, 0, outsideColor);
        var versionBeforeRegion = tile.Version;
        var colors = CreateColors(width, height);

        tile.SetRegion(x, z, width, height, colors);

        Assert.Equal(versionBeforeRegion + 1, tile.Version);
        var explored = new byte[MapTile.ExploredByteCount];
        tile.CopyExploredTo(explored);
        for (var localZ = 0; localZ < height; localZ++)
        {
            for (var localX = 0; localX < width; localX++)
            {
                var tileX = x + localX;
                var tileZ = z + localZ;
                var expected = colors[(localZ * width) + localX];
                Assert.True(tile.TryGetPixel(tileX, tileZ, out var actual));
                Assert.Equal(expected, actual);

                var pixelIndex = (tileZ * MapTile.Size) + tileX;
                Assert.NotEqual(0, explored[pixelIndex >> 3] & (1 << (pixelIndex & 7)));
            }
        }

        Assert.True(tile.TryGetPixel(63, 0, out var outsideActual));
        Assert.Equal(outsideColor, outsideActual);
        Assert.False(tile.TryGetPixel(62, 0, out _));
    }

    [Fact]
    public void Transparent_region_pixel_matches_SetPixel_and_clears_exploration()
    {
        var regionTile = new MapTile(0, 0);
        var pixelTile = new MapTile(0, 0);
        var opaque = new Rgba32(1, 2, 3, 255);
        var transparent = new Rgba32(11, 12, 13, 0);
        regionTile.SetPixel(5, 6, opaque);
        pixelTile.SetPixel(5, 6, opaque);

        regionTile.SetRegion(5, 6, 1, 1, [transparent]);
        pixelTile.SetPixel(5, 6, transparent);

        Assert.False(regionTile.TryGetPixel(5, 6, out _));
        Assert.Equal(CaptureState(pixelTile), CaptureState(regionTile));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1, 1, typeof(ArgumentOutOfRangeException))]
    [InlineData(64, 0, 1, 1, 1, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, -1, 1, 1, 1, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 64, 1, 1, 1, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, 0, 1, 0, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, -1, 1, 0, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, 1, 0, 0, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, 1, -1, 0, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, 65, 1, 65, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, 1, 65, 65, typeof(ArgumentOutOfRangeException))]
    [InlineData(63, 0, 2, 1, 2, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 63, 1, 2, 2, typeof(ArgumentOutOfRangeException))]
    [InlineData(0, 0, int.MaxValue, 2, 0, typeof(OverflowException))]
    [InlineData(0, 0, 2, int.MaxValue, 0, typeof(OverflowException))]
    [InlineData(1, 0, int.MaxValue, 1, 0, typeof(OverflowException))]
    [InlineData(0, 1, 1, int.MaxValue, 0, typeof(OverflowException))]
    [InlineData(0, 0, 2, 2, 3, typeof(ArgumentException))]
    [InlineData(0, 0, 2, 2, 5, typeof(ArgumentException))]
    public void Invalid_region_leaves_tile_bytes_and_version_unchanged(
        int x,
        int z,
        int width,
        int height,
        int colorCount,
        Type exceptionType)
    {
        var tile = new MapTile(0, 0);
        tile.SetPixel(3, 4, new Rgba32(21, 22, 23, 24));
        tile.SetPixel(6, 7, new Rgba32(31, 32, 33, 0));
        var before = CaptureState(tile);
        var colors = new Rgba32[colorCount];

        var exception = Record.Exception(() => tile.SetRegion(x, z, width, height, colors));

        Assert.IsType(exceptionType, exception);
        Assert.Equal(before, CaptureState(tile));
    }

    private static Rgba32[] CreateColors(int width, int height)
    {
        var colors = new Rgba32[width * height];
        for (var index = 0; index < colors.Length; index++)
        {
            colors[index] = new Rgba32(
                (byte)(index + 1),
                (byte)(index + 2),
                (byte)(index + 3),
                (byte)((index % 254) + 1));
        }

        return colors;
    }

    private static TileState CaptureState(MapTile tile)
    {
        var explored = new byte[MapTile.ExploredByteCount];
        var colors = new byte[MapTile.ColorByteCount];
        tile.CopyExploredTo(explored);
        tile.CopyColorsTo(colors);
        return new TileState(tile.Version, explored, colors);
    }

    private sealed record TileState(long Version, byte[] Explored, byte[] Colors)
    {
        public bool Equals(TileState? other) =>
            other is not null
            && Version == other.Version
            && Explored.AsSpan().SequenceEqual(other.Explored)
            && Colors.AsSpan().SequenceEqual(other.Colors);

        public override int GetHashCode() => HashCode.Combine(Version, Explored.Length, Colors.Length);
    }
}
