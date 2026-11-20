using System.IO.Compression;
using System.Security.Cryptography;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

/// <summary>
/// 2.9.0 lights relief from the screen's top-left; tiles recorded before that were lit from the
/// opposite side. Old tiles are mirrored on load as an approximation and their explored chunks
/// are recorded again when the player is near.
/// </summary>
public sealed class LegacyShadeReRecordTests
{
    private static readonly Rgba32 Grass = new(60, 140, 50, 255);

    [Theory]
    [InlineData((byte)1, (byte)255)]
    [InlineData((byte)100, (byte)156)]
    [InlineData((byte)128, (byte)128)]
    [InlineData((byte)200, (byte)56)]
    [InlineData((byte)255, (byte)1)]
    [InlineData((byte)0, (byte)0)]
    public void Legacy_shade_mirrors_around_neutral_and_unknown_stays_unknown(byte legacy, byte expected)
    {
        Assert.Equal(expected, TileCodec.MirrorLegacyHeightShade(legacy));
    }

    [Fact]
    public void Version_four_tile_is_read_mirrored_with_every_explored_chunk_flagged()
    {
        var legacy = CreateLegacyShadedTile();

        var loaded = TileCodec.Read(ToVersion(legacy, version: 4));

        Assert.True(loaded.TryGetTerrainPixel(0, 0, out var first));
        Assert.Equal((byte)156, first.HeightShade);
        Assert.True(loaded.TryGetTerrainPixel(20, 37, out var second));
        Assert.Equal((byte)56, second.HeightShade);
        Assert.Equal((ushort)((1 << 0) | (1 << 9)), loaded.StaleShadeChunks);
        Assert.False(loaded.IsChunkShadeCurrent(0, 0));
        Assert.False(loaded.IsChunkShadeCurrent(31, 47));
        Assert.True(loaded.IsChunkShadeCurrent(16, 0));
        Assert.True(loaded.IsChunkShadeCurrent(63, 63));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void Cave_layers_keep_legacy_shades_unmirrored(byte version)
    {
        var loaded = TileCodec.Read(ToVersion(CreateLegacyShadedTile(), version), mirrorLegacyHeightShades: false);

        Assert.True(loaded.TryGetTerrainPixel(0, 0, out var pixel));
        Assert.Equal((byte)100, pixel.HeightShade);
        Assert.Equal((ushort)0, loaded.StaleShadeChunks);
    }

    [Fact]
    public void Recording_a_whole_chunk_clears_only_that_chunk()
    {
        var loaded = TileCodec.Read(ToVersion(CreateLegacyShadedTile(), version: 4));

        // A partial region does not replace the chunk's mirrored shades, so it stays flagged.
        loaded.SetRegion(16, 32, 8, 8, Fill(Grass, 64), Fill(TerrainHeightShading.Neutral, 64));
        Assert.False(loaded.IsChunkShadeCurrent(20, 37));

        loaded.SetRegion(0, 0, 16, 16, Fill(Grass, 256), Fill(TerrainHeightShading.Neutral, 256));

        Assert.True(loaded.IsChunkShadeCurrent(0, 0));
        Assert.False(loaded.IsChunkShadeCurrent(20, 37));
        Assert.Equal((ushort)(1 << 9), loaded.StaleShadeChunks);
    }

    [Fact]
    public void Version_five_round_trips_shades_and_the_re_record_mask_unchanged()
    {
        var loaded = TileCodec.Read(ToVersion(CreateLegacyShadedTile(), version: 4));
        using var stream = new MemoryStream();
        TileCodec.Write(stream, loaded);
        stream.Position = 0;

        var reread = TileCodec.Read(stream);

        Assert.Equal(loaded.StaleShadeChunks, reread.StaleShadeChunks);
        Assert.True(reread.TryGetTerrainPixel(0, 0, out var pixel));
        Assert.Equal((byte)156, pixel.HeightShade);
        Assert.Equal(5, PayloadVersion(stream.ToArray()));
    }

    [Fact]
    public async Task Store_reads_a_v4_file_mirrored_and_never_rewrites_it()
    {
        using var directory = new TemporaryDirectory();
        var v4Path = Path.Combine(directory.Path, "0_0.v4.sctm");
        await File.WriteAllBytesAsync(
            v4Path,
            ToVersion(CreateLegacyShadedTile(), version: 4).ToArray(),
            TestContext.Current.CancellationToken);
        var v4Bytes = await File.ReadAllBytesAsync(v4Path, TestContext.Current.CancellationToken);

        var store = new ExplorationTileStore(directory.Path);
        Assert.True(store.ContainsKnownTile(0, 0));
        Assert.False(store.IsChunkShadeCurrent(0, 0, 0, 0));
        var tile = store.GetOrLoad(0, 0);
        Assert.True(tile.TryGetTerrainPixel(0, 0, out var pixel));
        Assert.Equal((byte)156, pixel.HeightShade);

        using (var lease = store.AcquireMutation(0, 0))
        {
            lease.Tile.SetRegion(0, 0, 16, 16, Fill(Grass, 256), Fill(TerrainHeightShading.Neutral, 256));
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(v4Bytes, await File.ReadAllBytesAsync(v4Path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(directory.Path, "0_0.v5.sctm")));
        var reopened = new ExplorationTileStore(directory.Path);
        Assert.True(reopened.IsChunkShadeCurrent(0, 0, 0, 0));
        Assert.False(reopened.IsChunkShadeCurrent(0, 0, 20, 37));
        Assert.True(reopened.GetOrLoad(0, 0).TryGetTerrainPixel(20, 37, out var kept));
        Assert.Equal((byte)56, kept.HeightShade);
    }

    [Fact]
    public async Task Store_prefers_v5_then_v4_then_the_pre_2_8_file()
    {
        using var directory = new TemporaryDirectory();
        var legacy = new MapTile(0, 0);
        legacy.SetPixel(1, 1, Grass, heightShade: 90);
        var v4 = new MapTile(0, 0);
        v4.SetPixel(1, 1, Grass, heightShade: 100);
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "0_0.sctm"),
            ToVersion(legacy, version: 3).ToArray(),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "0_0.v4.sctm"),
            ToVersion(v4, version: 4).ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Equal((byte)156, ShadeAt(new ExplorationTileStore(directory.Path), 1, 1));

        File.Delete(Path.Combine(directory.Path, "0_0.v4.sctm"));
        Assert.Equal((byte)166, ShadeAt(new ExplorationTileStore(directory.Path), 1, 1));

        var current = new MapTile(0, 0);
        current.SetPixel(1, 1, Grass, heightShade: 140);
        await using (var file = File.Create(Path.Combine(directory.Path, "0_0.v5.sctm")))
        {
            TileCodec.Write(file, current);
        }

        Assert.Equal((byte)140, ShadeAt(new ExplorationTileStore(directory.Path), 1, 1));
    }

    [Fact]
    public async Task Cave_layer_store_keeps_v4_shades()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "0_0.v4.sctm"),
            ToVersion(CreateLegacyShadedTile(), version: 4).ToArray(),
            TestContext.Current.CancellationToken);

        var store = new ExplorationTileStore(directory.Path, mirrorLegacyHeightShades: false);

        Assert.Equal((byte)100, ShadeAt(store, 0, 0));
        Assert.True(store.IsChunkShadeCurrent(0, 0, 0, 0));
    }

    [Fact]
    public async Task Recorder_records_a_flagged_chunk_again_and_then_counts_it_explored()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path);
        var recorder = new ExplorationRecorder(
            new TerrainMapSampler(new FakeTerrainMapSource(defaultContent: 1), TerrainMapSamplerTests.CreatePixelData()),
            store);
        var first = new TerrainChunkCoordinate(0, 0);
        var second = new TerrainChunkCoordinate(1, 0);
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(first));
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(second));
        await store.FlushAsync(TestContext.Current.CancellationToken);

        // The same area as 2.8.0 saved it: a v4 file, and no v5 yet.
        var recorded = await File.ReadAllBytesAsync(
            Path.Combine(directory.Path, "0_0.v5.sctm"),
            TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(directory.Path, "0_0.v5.sctm"));
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "0_0.v4.sctm"),
            ToVersion(recorded, version: 4).ToArray(),
            TestContext.Current.CancellationToken);

        var reopenedStore = new ExplorationTileStore(directory.Path);
        var reopened = new ExplorationRecorder(
            new TerrainMapSampler(new FakeTerrainMapSource(defaultContent: 1), TerrainMapSamplerTests.CreatePixelData()),
            reopenedStore);

        Assert.False(reopened.IsChunkFullyExplored(first));
        Assert.False(reopened.IsChunkFullyExplored(second));
        Assert.Equal(ExplorationRecordResult.Recorded, reopened.RecordChunk(first));
        Assert.True(reopened.IsChunkFullyExplored(first));
        Assert.False(reopened.IsChunkFullyExplored(second));
    }

    private static byte ShadeAt(ExplorationTileStore store, int x, int z)
    {
        Assert.True(store.GetOrLoad(0, 0).TryGetTerrainPixel(x, z, out var pixel));
        return pixel.HeightShade;
    }

    /// <summary>Chunk 0 shaded 100 throughout, one pixel of chunk 9 (local 20, 37) shaded 200.</summary>
    private static MapTile CreateLegacyShadedTile()
    {
        var tile = new MapTile(0, 0);
        tile.SetRegion(0, 0, 16, 16, Fill(Grass, 256), Fill((byte)100, 256));
        tile.SetPixel(20, 37, Grass, heightShade: 200);
        return tile;
    }

    private static T[] Fill<T>(T value, int count) => Enumerable.Repeat(value, count).ToArray();

    private static MemoryStream ToVersion(MapTile tile, byte version)
    {
        using var current = new MemoryStream();
        TileCodec.Write(current, tile);
        return ToVersion(current.ToArray(), version);
    }

    internal static MemoryStream ToVersion(byte[] compressedCurrent, byte version) =>
        LegacyTileFormat.Convert(compressedCurrent, version);

    private static int PayloadVersion(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var payload = new MemoryStream();
        inflater.CopyTo(payload);
        return payload.ToArray()[4];
    }
}

/// <summary>Rewrites a current (version 5) tile file as an older codec version would have.</summary>
internal static class LegacyTileFormat
{
    private const int ChecksumLength = 32;
    private const int StaleShadeMaskLength = 2;

    public static MemoryStream Convert(byte[] compressedCurrent, byte version)
    {
        using var input = new MemoryStream(compressedCurrent);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var payloadStream = new MemoryStream();
        inflater.CopyTo(payloadStream);
        var currentPayload = payloadStream.ToArray();
        var stripped = version switch
        {
            >= 4 => StaleShadeMaskLength,
            3 => MapTile.SeasonByteCount + StaleShadeMaskLength,
            _ => MapTile.HeightShadeByteCount + MapTile.SeasonByteCount + StaleShadeMaskLength,
        };
        var payload = new byte[currentPayload.Length - stripped];
        var checksumOffset = payload.Length - ChecksumLength;
        currentPayload.AsSpan(0, checksumOffset).CopyTo(payload);
        payload[4] = version;
        SHA256.HashData(payload.AsSpan(4, checksumOffset - 4)).CopyTo(payload, checksumOffset);

        var result = new MemoryStream();
        using (var deflater = new DeflateStream(result, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(payload);
        }

        result.Position = 0;
        return result;
    }
}
