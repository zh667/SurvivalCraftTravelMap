using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class SeasonalMapTests : IDisposable
{
    private static readonly Rgba32 Grass = new(52, 150, 37, 255);
    private static readonly MapSeason Summer = new(0, PredictsFreezing: true, WeatherEffects: true);
    private static readonly MapSeason DeepWinter = new(-24, PredictsFreezing: true, WeatherEffects: true);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "SurvivalcraftTravelMap.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- the colour rule -------------------------------------------------------------------

    [Fact]
    public void Land_takes_snow_exactly_when_the_season_reaches_its_limit()
    {
        var sample = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Land, false, -10));

        Assert.Equal(Grass, SeasonalTerrainColor.Resolve(Grass, sample, Summer with { Temperature = -9 }));
        Assert.Equal(
            SeasonalTerrainColor.Snow,
            SeasonalTerrainColor.Resolve(Grass, sample, Summer with { Temperature = -10 }));
    }

    [Fact]
    public void Snow_seen_in_winter_melts_on_the_map_in_summer()
    {
        // The player's report: a region left snowy in winter still showed snow in summer.
        var sample = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Land, true, -6));

        Assert.Equal(Grass, SeasonalTerrainColor.Resolve(Grass, sample, Summer));
        Assert.Equal(SeasonalTerrainColor.Snow, SeasonalTerrainColor.Resolve(Grass, sample, DeepWinter));
    }

    [Fact]
    public void Without_weather_effects_frozen_land_only_keeps_snow_it_already_had()
    {
        var noWeather = DeepWinter with { WeatherEffects = false };
        var bare = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Land, false, 0));
        var snowy = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Land, true, 0));

        Assert.Equal(Grass, SeasonalTerrainColor.Resolve(Grass, bare, noWeather));
        Assert.Equal(SeasonalTerrainColor.Snow, SeasonalTerrainColor.Resolve(Grass, snowy, noWeather));
    }

    [Fact]
    public void Static_environment_shows_terrain_as_recorded()
    {
        var staticWorld = new MapSeason(-24, PredictsFreezing: false, WeatherEffects: true);
        var bare = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Land, false, 0));
        var snowy = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Land, true, -30));

        Assert.Equal(Grass, SeasonalTerrainColor.Resolve(Grass, bare, staticWorld));
        Assert.Equal(SeasonalTerrainColor.Snow, SeasonalTerrainColor.Resolve(Grass, snowy, staticWorld));
    }

    [Fact]
    public void Water_ices_over_and_melts_and_fixed_pixels_never_change()
    {
        var water = new Rgba32(0, 40, 85, 255);
        var shore = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Water, false, -12));
        var rock = MapSeasonCode.ToSample(MapSeasonCode.Create(MapSeasonKind.Fixed, false, 0));

        Assert.Equal(SeasonalTerrainColor.Ice, SeasonalTerrainColor.Resolve(water, shore, DeepWinter));
        Assert.Equal(water, SeasonalTerrainColor.Resolve(water, shore, Summer));
        Assert.Equal(water, SeasonalTerrainColor.Resolve(water, rock, DeepWinter));
    }

    [Fact]
    public void Season_code_round_trips_and_clamps_its_limit()
    {
        var code = MapSeasonCode.Create(MapSeasonKind.Water, true, -100);
        Assert.Equal(MapSeasonKind.Water, MapSeasonCode.GetKind(code));
        Assert.True(MapSeasonCode.WasObservedFrozen(code));
        Assert.Equal(MapSeasonCode.MinimumLimit, MapSeasonCode.GetLimit(code));
        Assert.Equal(MapSeasonCode.MaximumLimit, MapSeasonCode.GetLimit(
            MapSeasonCode.Create(MapSeasonKind.Land, false, 100)));
        Assert.NotEqual(0, MapSeasonCode.Create(MapSeasonKind.Fixed, false, 0));
    }

    [Fact]
    public void Published_season_survives_negative_temperatures()
    {
        var previous = MapSeason.Current;
        try
        {
            MapSeason.Current = DeepWinter with { WeatherEffects = false };
            Assert.Equal(DeepWinter with { WeatherEffects = false }, MapSeason.Current);
        }
        finally
        {
            MapSeason.Current = previous;
        }
    }

    // ---- recording ---------------------------------------------------------------------------

    [Fact]
    public void Snow_covered_column_records_the_ground_under_it_and_its_freeze_limit()
    {
        var source = new SeasonTerrain { BaseTemperature = 6 };
        source.Top[(0, 0)] = (65, TerrainMapSampler.SnowContent);
        source.Cells[(0, 64, 0)] = 2; // dirt under the snow layer

        var (color, code) = SampleOne(source, 0, 0);

        Assert.Equal(new Rgba32(100, 70, 40, 255), color);
        Assert.Equal(MapSeasonKind.Land, MapSeasonCode.GetKind(code));
        Assert.True(MapSeasonCode.WasObservedFrozen(code));

        // Frozen iff 6 + season + adjustment(64) <= 0 with adjustment(64) = 0: limit -6.
        Assert.Equal(-6, MapSeasonCode.GetLimit(code));
    }

    [Fact]
    public void Snow_capable_bare_ground_is_land_and_rock_is_fixed()
    {
        var source = new SeasonTerrain { BaseTemperature = 3 };
        source.Top[(0, 0)] = (64, 2);
        source.SnowCapable.Add(2);
        source.Top[(1, 0)] = (64, 1);

        var (_, ground) = SampleOne(source, 0, 0);
        var (_, rock) = SampleOne(source, 1, 0);

        Assert.Equal(MapSeasonKind.Land, MapSeasonCode.GetKind(ground));
        Assert.False(MapSeasonCode.WasObservedFrozen(ground));
        Assert.Equal(-3, MapSeasonCode.GetLimit(ground));
        Assert.Equal(MapSeasonKind.Fixed, MapSeasonCode.GetKind(rock));
    }

    [Fact]
    public void Shore_water_and_seen_ice_freeze_but_open_water_does_not()
    {
        var source = new SeasonTerrain { BaseTemperature = 2, DefaultContent = TerrainMapSampler.WaterContent };
        source.Top[(0, 0)] = (64, TerrainMapSampler.WaterContent);
        source.Cells[(1, 64, 0)] = 1; // rock beside it
        source.Top[(5, 5)] = (64, TerrainMapSampler.WaterContent);
        source.Top[(9, 9)] = (64, TerrainMapSampler.IceContent);

        var (_, shore) = SampleOne(source, 0, 0);
        var (_, open) = SampleOne(source, 5, 5);
        var (iceColor, ice) = SampleOne(source, 9, 9);

        Assert.Equal(MapSeasonKind.Water, MapSeasonCode.GetKind(shore));
        Assert.Equal(MapSeasonKind.Fixed, MapSeasonCode.GetKind(open));
        Assert.Equal(MapSeasonKind.Water, MapSeasonCode.GetKind(ice));
        Assert.True(MapSeasonCode.WasObservedFrozen(ice));
        Assert.Equal(new Rgba32(0, 40, 85, 255), iceColor);
    }

    [Fact]
    public void Freeze_limit_follows_the_editions_own_rule()
    {
        // The plugin edition clamps the seasonal temperature at 0, so low ground never freezes.
        var source = new SeasonTerrain
        {
            BaseTemperature = 4,
            FreezeRule = (baseTemperature, y, season) =>
                Math.Max(baseTemperature + season, 0) + TerrainMapSampler.GetTemperatureAdjustmentAtHeight(y) <= 0,
        };
        source.Top[(0, 0)] = (40, 2);
        source.SnowCapable.Add(2);

        var (_, code) = SampleOne(source, 0, 0);

        Assert.Equal(MapSeasonCode.MinimumLimit, MapSeasonCode.GetLimit(code));
    }

    // ---- storage -----------------------------------------------------------------------------

    [Fact]
    public void Codec_round_trips_season_codes()
    {
        var tile = new MapTile(3, -4);
        var code = MapSeasonCode.Create(MapSeasonKind.Land, true, -7);
        tile.SetRegion(2, 3, 1, 1, [Grass], [TerrainHeightShading.Neutral], [code]);
        using var stream = new MemoryStream();
        TileCodec.Write(stream, tile);
        stream.Position = 0;

        var loaded = TileCodec.Read(stream);

        Assert.True(loaded.TryGetTerrainPixel(2, 3, out var pixel));
        Assert.Equal(MapSeasonCode.ToSample(code), pixel.Season);
        Assert.True(loaded.IsRegionFullySeasonTracked(2, 3, 1, 1));
    }

    [Fact]
    public async Task New_format_never_overwrites_the_file_older_versions_read()
    {
        Directory.CreateDirectory(_directory);
        var legacy = new MapTile(0, 0);
        legacy.SetPixel(1, 1, Grass);
        var legacyPath = Path.Combine(_directory, "0_0.sctm");
        await using (var file = File.Create(legacyPath))
        {
            TileCodec.Write(file, legacy);
        }

        var legacyBytes = await File.ReadAllBytesAsync(legacyPath, TestContext.Current.CancellationToken);
        var store = new ExplorationTileStore(_directory);
        var loaded = store.GetOrLoad(0, 0);
        Assert.True(loaded.TryGetPixel(1, 1, out _));
        Assert.False(loaded.IsRegionFullySeasonTracked(0, 0, 16, 16));

        using (var lease = store.AcquireMutation(0, 0))
        {
            lease.Tile.SetRegion(
                0,
                0,
                1,
                1,
                [Grass],
                [TerrainHeightShading.Neutral],
                [MapSeasonCode.Create(MapSeasonKind.Land, false, -5)]);
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_directory, "0_0.v4.sctm")));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacyPath, TestContext.Current.CancellationToken));
        var reopened = new ExplorationTileStore(_directory).GetOrLoad(0, 0);
        Assert.True(reopened.TryGetTerrainPixel(0, 0, out var pixel));
        Assert.Equal(MapSeasonKind.Land, pixel.Season.Land == 255 ? MapSeasonKind.Land : MapSeasonKind.None);
        Assert.True(reopened.TryGetPixel(1, 1, out _));
    }

    [Fact]
    public void Old_areas_count_as_unexplored_until_re_recorded_with_seasons()
    {
        Directory.CreateDirectory(_directory);
        var store = new ExplorationTileStore(_directory);
        var source = new SeasonTerrain { BaseTemperature = 5 };
        var recorder = new ExplorationRecorder(new TerrainMapSampler(source, SeasonTerrain.Palette), store);
        var chunk = new TerrainChunkCoordinate(0, 0);
        using (var lease = store.AcquireMutation(0, 0))
        {
            var colors = Enumerable.Repeat(Grass, 256).ToArray();
            var shades = Enumerable.Repeat(TerrainHeightShading.Neutral, 256).ToArray();
            lease.Tile.SetRegion(0, 0, 16, 16, colors, shades);
        }

        Assert.False(recorder.IsChunkFullyExplored(chunk));
        Assert.Equal(ExplorationRecordResult.Recorded, recorder.RecordChunk(chunk));
        Assert.True(recorder.IsChunkFullyExplored(chunk));
    }

    [Fact]
    public void Zoomed_out_region_reports_the_share_and_mean_limit_of_each_kind()
    {
        var tile = new MapTile(0, 0);
        var colors = Enumerable.Repeat(Grass, 4).ToArray();
        var shades = Enumerable.Repeat(TerrainHeightShading.Neutral, 4).ToArray();
        tile.SetRegion(0, 0, 2, 2, colors, shades,
        [
            MapSeasonCode.Create(MapSeasonKind.Land, false, -4),
            MapSeasonCode.Create(MapSeasonKind.Land, true, -8),
            MapSeasonCode.Create(MapSeasonKind.Water, false, -20),
            MapSeasonCode.Create(MapSeasonKind.Fixed, false, 0),
        ]);
        var snapshot = tile.CreateVersionedSnapshot().Snapshot;

        Assert.True(snapshot.TryGetExploredTerrainRegion(0, 0, 2, 2, out var region));

        Assert.Equal(128, region.Season.Land);
        Assert.Equal(64, region.Season.LandFrozen);
        Assert.Equal(64, region.Season.Water);
        Assert.Equal(-6, region.Season.LandLimit);
        Assert.Equal(-20, region.Season.WaterLimit);
    }

    // ---- presentation ------------------------------------------------------------------------

    [Fact]
    public void Season_step_repaints_the_mini_map_without_blanking_it()
    {
        Directory.CreateDirectory(_directory);
        var store = new ExplorationTileStore(_directory);
        using (var lease = store.AcquireMutation(0, 0))
        {
            var colors = Enumerable.Repeat(Grass, MapTile.Size * MapTile.Size).ToArray();
            var shades = Enumerable.Repeat(TerrainHeightShading.Neutral, colors.Length).ToArray();
            var seasons = Enumerable
                .Repeat(MapSeasonCode.Create(MapSeasonKind.Land, false, -10), colors.Length)
                .ToArray();
            lease.Tile.SetRegion(0, 0, MapTile.Size, MapTile.Size, colors, shades, seasons);
        }

        var season = Summer;
        var model = new MiniMapTerrainTextureModel { SeasonSource = () => season };
        var source = new TileStoreMapPixelSource(store);
        var transform = new MapTransform(new Vector2(32f, 32f), 1f, new Vector2(40f));
        var time = 0.0;
        for (var frame = 0; frame < 64 && (frame == 0 || model.DirtyCellCount > 0); frame++)
        {
            model.Update(source, transform, 0f, time += 1.0);
        }

        var center = TexelAt(model, 32, 32);
        Assert.Equal(Grass, center);

        season = DeepWinter;
        model.Update(source, transform, 0f, time += 1.0);
        Assert.NotEqual(default, TexelAt(model, 32, 32));
        for (var frame = 0; frame < 64 && model.DirtyCellCount > 0; frame++)
        {
            model.Update(source, transform, 0f, time += 1.0);
        }

        Assert.Equal(SeasonalTerrainColor.Snow, TexelAt(model, 32, 32));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static Rgba32 TexelAt(MiniMapTerrainTextureModel model, int worldX, int worldZ)
    {
        var x = (int)((worldX - model.OriginX) / model.TexelBlocks);
        var z = (int)((worldZ - model.OriginZ) / model.TexelBlocks);
        return model.Pixels[(z * model.TextureSize) + x];
    }

    private static (Rgba32 Color, byte Code) SampleOne(SeasonTerrain source, int x, int z)
    {
        var sampler = new TerrainMapSampler(source, SeasonTerrain.Palette);
        var chunk = TerrainChunkCoordinate.FromWorld(x, z);
        var colors = new Rgba32[TerrainChunkCoordinate.PixelCount];
        var shades = new byte[TerrainChunkCoordinate.PixelCount];
        var seasons = new byte[TerrainChunkCoordinate.PixelCount];
        Assert.True(sampler.TrySampleChunk(chunk, colors, shades, seasons));
        var index = ((z - chunk.OriginZ) * TerrainChunkCoordinate.Size) + (x - chunk.OriginX);
        return (colors[index], seasons[index]);
    }

    private sealed class SeasonTerrain : ITerrainMapSource
    {
        public static readonly IReadOnlyDictionary<int, BlockPixelData> Palette =
            TerrainMapSamplerTests.CreatePixelData(overrides: new Dictionary<int, BlockPixelData>
            {
                [1] = new(1, new Rgba32(97, 97, 97, 255), false),
                [2] = new(2, new Rgba32(100, 70, 40, 255), false),
                [18] = new(18, new Rgba32(0, 40, 85, 255), false),
                [61] = new(61, SeasonalTerrainColor.Snow, false),
                [62] = new(62, SeasonalTerrainColor.Ice, false),
            });

        public Dictionary<(int X, int Z), (int Height, int Content)> Top { get; } = [];

        public Dictionary<(int X, int Y, int Z), int> Cells { get; } = [];

        public HashSet<int> SnowCapable { get; } = [];

        public int BaseTemperature { get; init; } = 8;

        public int DefaultContent { get; init; } = 1;

        public Func<int, int, int, bool>? FreezeRule { get; init; }

        public bool IsColumnReady(int x, int z) => true;

        public int GetTopHeight(int x, int z) => Top.TryGetValue((x, z), out var top) ? top.Height : 64;

        public int GetContent(int x, int y, int z)
        {
            if (Cells.TryGetValue((x, y, z), out var content))
            {
                return content;
            }

            return Top.TryGetValue((x, z), out var top) && top.Height == y ? top.Content : DefaultContent;
        }

        public bool IsFluidCell(int x, int y, int z) => GetContent(x, y, z) == TerrainMapSampler.WaterContent;

        public int GetSeasonalTemperature(int x, int z) => BaseTemperature;

        public int GetSeasonalHumidity(int x, int z) => 8;

        public bool IsPlaceFrozenAtSeason(int x, int z, int y, int seasonTemperature) =>
            FreezeRule?.Invoke(BaseTemperature, y, seasonTemperature)
            ?? BaseTemperature + seasonTemperature + TerrainMapSampler.GetTemperatureAdjustmentAtHeight(y) <= 0;

        public bool CanHoldSnow(int x, int y, int z) => SnowCapable.Contains(GetContent(x, y, z));
    }
}
