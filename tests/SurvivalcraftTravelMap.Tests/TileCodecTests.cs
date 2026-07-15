using System.IO.Compression;
using System.Security.Cryptography;
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
        tile.SetPixel(63, 0, new Rgba32(1, 2, 3, 255), heightShade: 96);

        using var stream = new MemoryStream();
        TileCodec.Write(stream, tile);
        stream.Position = 0;

        var loaded = TileCodec.Read(stream);

        Assert.Equal(-2, loaded.TileX);
        Assert.Equal(3, loaded.TileZ);
        Assert.True(loaded.TryGetPixel(63, 0, out var color));
        Assert.Equal(new Rgba32(1, 2, 3, 255), color);
        Assert.True(loaded.TryGetTerrainPixel(63, 0, out var terrainPixel));
        Assert.Equal((byte)96, terrainPixel.HeightShade);
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

    [Fact]
    public void Codec_reads_version_one_tiles_and_upgrades_fixed_and_environment_colors()
    {
        var tile = new MapTile(-7, 11);
        tile.SetPixel(1, 2, new Rgba32(112, 106, 99, 255));
        tile.SetPixel(3, 4, new Rgba32(79, 225, 56, 255));
        using var current = new MemoryStream();
        TileCodec.Write(current, tile);
        using var legacy = ConvertCurrentTileToPaletteOnly(current.ToArray(), version: 1);

        var loaded = TileCodec.Read(legacy);

        Assert.True(loaded.TryGetPixel(1, 2, out var stone));
        Assert.Equal(new Rgba32(97, 97, 97, 255), stone);
        Assert.True(loaded.TryGetPixel(3, 4, out var grass));
        Assert.Equal(new Rgba32(52, 150, 37, 255), grass);
        Assert.True(loaded.TryGetTerrainPixel(1, 2, out var terrainPixel));
        Assert.Equal(TerrainHeightShading.Unknown, terrainPixel.HeightShade);
    }

    [Fact]
    public void Codec_reads_version_two_palette_tiles_and_marks_height_shading_for_lazy_upgrade()
    {
        var color = new Rgba32(97, 97, 97, 255);
        var tile = new MapTile(2, -3);
        tile.SetPixel(5, 6, color, heightShade: 177);
        using var current = new MemoryStream();
        TileCodec.Write(current, tile);
        using var versionTwo = ConvertCurrentTileToPaletteOnly(current.ToArray(), version: 2);

        var loaded = TileCodec.Read(versionTwo);

        Assert.True(loaded.TryGetTerrainPixel(5, 6, out var terrainPixel));
        Assert.Equal(color, terrainPixel.Color);
        Assert.Equal(TerrainHeightShading.Unknown, terrainPixel.HeightShade);
        Assert.False(loaded.IsRegionFullyHeightShaded(5, 6, 1, 1));
    }

    private static MemoryStream ConvertCurrentTileToPaletteOnly(byte[] compressed, byte version)
    {
        using var input = new MemoryStream(compressed);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var payloadStream = new MemoryStream();
        inflater.CopyTo(payloadStream);
        const int checksumLength = 32;
        var currentPayload = payloadStream.ToArray();
        var payload = new byte[currentPayload.Length - MapTile.HeightShadeByteCount];
        var checksumOffset = payload.Length - checksumLength;
        currentPayload.AsSpan(0, checksumOffset).CopyTo(payload);
        payload[4] = version;
        var checksum = SHA256.HashData(payload.AsSpan(4, checksumOffset - 4));
        checksum.CopyTo(payload, checksumOffset);

        var result = new MemoryStream();
        using (var deflater = new DeflateStream(result, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(payload);
        }

        result.Position = 0;
        return result;
    }
}
