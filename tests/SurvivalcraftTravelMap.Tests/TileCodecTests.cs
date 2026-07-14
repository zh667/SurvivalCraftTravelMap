using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TileCodecTests
{
    [Fact]
    public void Codec_round_trips_coordinates_exploration_and_rgba()
    {
        var tile = new MapTile(-2, 3);
        tile.SetPixel(63, 0, new Rgba32(1, 2, 3, 255));

        using var stream = new MemoryStream();
        TileCodec.Write(stream, tile);
        stream.Position = 0;

        var loaded = TileCodec.Read(stream);

        Assert.Equal(-2, loaded.TileX);
        Assert.Equal(3, loaded.TileZ);
        Assert.True(loaded.TryGetPixel(63, 0, out var color));
        Assert.Equal(new Rgba32(1, 2, 3, 255), color);
        Assert.False(loaded.TryGetPixel(0, 0, out _));
    }

    [Fact]
    public void Codec_rejects_a_corrupted_compressed_byte()
    {
        var tile = new MapTile(4, -5);
        tile.SetPixel(1, 2, new Rgba32(7, 8, 9, 10));
        using var stream = new MemoryStream();
        TileCodec.Write(stream, tile);
        var bytes = stream.ToArray();
        bytes[bytes.Length / 2] ^= 0x40;

        using var corrupted = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => TileCodec.Read(corrupted));
    }
}
