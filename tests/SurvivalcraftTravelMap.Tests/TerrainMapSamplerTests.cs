using System.Text;
using System.Text.Json;
using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TerrainMapSamplerTests
{
    private static readonly int[] TransparentTopContents = [28, 99, 19, 174, 25];
    private static readonly int[] WaterVariantContents = [226, 229, 232, 233];

    [Theory]
    [MemberData(nameof(TransparentTopCases))]
    public void Transparent_top_content_reveals_the_block_one_cell_below(int transparentContent)
    {
        var source = new FakeTerrainMapSource(topHeight: 70, defaultContent: 1);
        source.SetContent(12, 70, -8, transparentContent);
        source.SetContent(12, 69, -8, 2);
        var sampler = new TerrainMapSampler(source, CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
        {
            [1] = Pixel(1, 10, 20, 30),
            [2] = Pixel(2, 40, 50, 60),
        }));

        var color = sampler.Sample(12, -8);

        Assert.Equal(new Rgba32(40, 50, 60, 255), color);
        Assert.Equal([(12, 70, -8), (12, 69, -8)], source.ContentCalls);
    }

    [Theory]
    [MemberData(nameof(WaterVariantCases))]
    public void Water_variants_use_the_base_water_color(int waterContent)
    {
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: waterContent);
        var sampler = new TerrainMapSampler(source, CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
        {
            [18] = Pixel(18, 4, 80, 120),
            [waterContent] = Pixel(waterContent, 240, 10, 10),
        }));

        var color = sampler.Sample(3, 5);

        Assert.Equal(new Rgba32(4, 80, 120, 255), color);
    }

    [Theory]
    [InlineData(8, 151, 184, 195)]
    [InlineData(12, 96, 161, 123)]
    [InlineData(13, 76, 181, 96)]
    [InlineData(14, 96, 161, 155)]
    [InlineData(18, 0, 0, 120)]
    [InlineData(225, 90, 141, 165)]
    [InlineData(256, 146, 191, 176)]
    public void Environment_sensitive_contents_use_their_daytime_palette(
        int content,
        byte expectedR,
        byte expectedG,
        byte expectedB)
    {
        var source = new FakeTerrainMapSource(
            topHeight: 64,
            defaultContent: content,
            seasonalTemperature: 0,
            seasonalHumidity: 4);
        var sampler = new TerrainMapSampler(source, CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
        {
            [content] = Pixel(content, 255, 255, 255, changesWithEnvironment: true),
        }));

        var color = sampler.Sample(-2, 9);

        Assert.Equal(new Rgba32(expectedR, expectedG, expectedB, 255), color);
        Assert.Equal([(-2, 9)], source.TemperatureCalls);
        Assert.Equal([(-2, 9)], source.HumidityCalls);
    }

    [Fact]
    public void Environmental_palette_multiplies_the_configured_base_color_and_adjusts_temperature_for_height()
    {
        var source = new FakeTerrainMapSource(
            topHeight: 54,
            defaultContent: 8,
            seasonalTemperature: 0,
            seasonalHumidity: 4);
        var sampler = new TerrainMapSampler(source, CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
        {
            [8] = Pixel(8, 128, 64, 32, changesWithEnvironment: true),
        }));

        var color = sampler.Sample(1, 2);

        // Temperature 0 receives a +1 height adjustment at Y=54. The grass palette
        // therefore interpolates its cold and warm corners by 1/8 before multiplication.
        Assert.Equal(new Rgba32(79, 46, 22, 255), color);
    }

    [Fact]
    public void Ordinary_content_returns_the_configured_base_color_without_environment_or_night_tint()
    {
        var source = new FakeTerrainMapSource(topHeight: 90, defaultContent: 7);
        var sampler = new TerrainMapSampler(source, CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
        {
            [7] = Pixel(7, 211, 196, 149, alpha: 255),
        }));

        var color = sampler.Sample(100, 200);

        Assert.Equal(new Rgba32(211, 196, 149, 255), color);
        Assert.Empty(source.TemperatureCalls);
        Assert.Empty(source.HumidityCalls);
    }

    [Fact]
    public void Json_dictionary_is_loaded_once_and_sampling_does_not_read_the_stream_again()
    {
        using var inner = CreatePixelJsonStream();
        using var stream = new CountingReadStream(inner);
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var sampler = new TerrainMapSampler(source, stream);
        var readsAfterConstruction = stream.ReadCount;

        sampler.Sample(0, 0);
        sampler.Sample(1, 1);

        Assert.True(readsAfterConstruction > 0);
        Assert.Equal(readsAfterConstruction, stream.ReadCount);
    }

    [Fact]
    public void Json_dictionary_rejects_a_missing_color_key_with_a_descriptive_error()
    {
        using var stream = CreatePixelJsonStream(excludedKey: 42);

        var exception = Assert.Throws<InvalidDataException>(
            () => new TerrainMapSampler(new FakeTerrainMapSource(), stream));

        Assert.Contains("42", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0..256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_dictionary_rejects_keys_outside_the_supported_range()
    {
        using var stream = CreatePixelJsonStream(addExtraKey: true);

        var exception = Assert.Throws<InvalidDataException>(
            () => new TerrainMapSampler(new FakeTerrainMapSource(), stream));

        Assert.Contains("257", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0..256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_color_asset_contains_exactly_257_valid_entries()
    {
        var path = Path.Combine(
            TestPaths.RepositoryRoot,
            "src",
            "SurvivalcraftTravelMap",
            "Assets",
            "BlockPixelColor.json");
        using var stream = File.OpenRead(path);

        var entries = BlockPixelData.LoadDictionary(stream);

        Assert.Equal(Enumerable.Range(0, 257), entries.Keys.Order());
        Assert.Equal(
            [8, 12, 13, 14, 18, 225, 256],
            entries.Values
                .Where(entry => entry.NeedChangeWithEnvironment)
                .Select(entry => entry.BlockIndex)
                .Order()
                .ToArray());

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        Assert.NotEqual("656ECD47A8C01514A3B4F6D90E6EC7AFE2B0590A31758825C7A64B311FC0D55A", hash);

        Assert.Equal(new Rgba32(0, 0, 0, 0), entries[0].Color); // Air.
        Assert.Equal(new Rgba32(112, 106, 99, 255), entries[1].Color); // Stone.
        Assert.Equal(new Rgba32(126, 91, 58, 255), entries[2].Color); // Dirt.
        Assert.Equal(new Rgba32(215, 195, 139, 255), entries[7].Color); // Sand.
        Assert.Equal(new Rgba32(131, 91, 52, 255), entries[9].Color); // Oak wood.
        Assert.Equal(new Rgba32(238, 85, 30, 255), entries[92].Color); // Magma.
        Assert.Equal(new Rgba32(52, 122, 65, 255), entries[127].Color); // Cactus.
        Assert.Equal(new Rgba32(255, 255, 255, 255), entries[8].Color); // Runtime grass tint.
        Assert.Equal(new Rgba32(255, 255, 255, 255), entries[18].Color); // Runtime water tint.
    }

    [Fact]
    public void Project_palette_generator_reproduces_the_bundled_asset()
    {
        using var temporary = new TemporaryDirectory();
        var generatedPath = Path.Combine(temporary.Path, "BlockPixelColor.json");
        var generator = Path.Combine(TestPaths.RepositoryRoot, "tools", "Generate-BlockPalette.ps1");

        var result = PowerShellRunner.Run(generator, generatedPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(
                TestPaths.RepositoryRoot,
                "src",
                "SurvivalcraftTravelMap",
                "Assets",
                "BlockPixelColor.json")),
            File.ReadAllBytes(generatedPath));
    }

    public static TheoryData<int> TransparentTopCases => new(TransparentTopContents);

    public static TheoryData<int> WaterVariantCases => new(WaterVariantContents);

    internal static IReadOnlyDictionary<int, BlockPixelData> CreatePixelData(
        IReadOnlyDictionary<int, BlockPixelData>? overrides = null)
    {
        var result = Enumerable.Range(0, 257)
            .ToDictionary(index => index, index => Pixel(index, 1, 2, 3));

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static BlockPixelData Pixel(
        int index,
        byte r,
        byte g,
        byte b,
        byte alpha = 255,
        bool changesWithEnvironment = false) =>
        new(index, new Rgba32(r, g, b, alpha), changesWithEnvironment);

    private static MemoryStream CreatePixelJsonStream(int? excludedKey = null, bool addExtraKey = false)
    {
        var data = Enumerable.Range(0, 257)
            .Where(index => index != excludedKey)
            .ToDictionary(
                index => index.ToString(),
                index => new
                {
                    BlockIndex = index,
                    Color = new { R = index % 256, G = 20, B = 30, A = 255 },
                    NeedChangeWithEnvironment = false,
                });

        if (addExtraKey)
        {
            data["257"] = new
            {
                BlockIndex = 257,
                Color = new { R = 1, G = 2, B = 3, A = 255 },
                NeedChangeWithEnvironment = false,
            };
        }

        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(data));
    }
}

internal sealed class FakeTerrainMapSource(
    int topHeight = 64,
    int defaultContent = 1,
    int seasonalTemperature = 8,
    int seasonalHumidity = 8) : ITerrainMapSource
{
    private readonly Dictionary<(int X, int Y, int Z), int> _contents = [];

    internal List<(int X, int Y, int Z)> ContentCalls { get; } = [];

    internal List<(int X, int Z)> SampledColumns { get; } = [];

    internal List<(int X, int Z)> TemperatureCalls { get; } = [];

    internal List<(int X, int Z)> HumidityCalls { get; } = [];

    internal void SetContent(int x, int y, int z, int content) => _contents[(x, y, z)] = content;

    public int GetTopHeight(int x, int z)
    {
        SampledColumns.Add((x, z));
        return topHeight;
    }

    public int GetContent(int x, int y, int z)
    {
        ContentCalls.Add((x, y, z));
        return _contents.GetValueOrDefault((x, y, z), defaultContent);
    }

    public int GetSeasonalTemperature(int x, int z)
    {
        TemperatureCalls.Add((x, z));
        return seasonalTemperature;
    }

    public int GetSeasonalHumidity(int x, int z)
    {
        HumidityCalls.Add((x, z));
        return seasonalHumidity;
    }
}

internal sealed class CountingReadStream(Stream inner) : Stream
{
    internal int ReadCount { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCount++;
        return inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        ReadCount++;
        return inner.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
