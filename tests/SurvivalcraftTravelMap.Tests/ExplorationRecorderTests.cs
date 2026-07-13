using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class ExplorationRecorderTests
{
    [Fact]
    public async Task Visible_square_crossing_63_63_updates_and_marks_only_four_touched_tiles_dirty()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path);
        store.GetOrLoad(5, 5);
        var recorder = new ExplorationRecorder(sampler, store);

        recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["0_0.sctm", "0_1.sctm", "1_0.sctm", "1_1.sctm"],
            Directory.GetFiles(directory.Path, "*.sctm").Select(path => Path.GetFileName(path)!).Order().ToArray());

        Assert.True(store.GetOrLoad(0, 0).TryGetPixel(62, 62, out _));
        Assert.True(store.GetOrLoad(0, 1).TryGetPixel(62, 0, out _));
        Assert.True(store.GetOrLoad(1, 0).TryGetPixel(0, 62, out _));
        Assert.True(store.GetOrLoad(1, 1).TryGetPixel(0, 0, out _));
    }

    [Fact]
    public void Recorder_never_samples_outside_the_requested_visible_square()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var recorder = new ExplorationRecorder(sampler, new ExplorationTileStore(directory.Path));

        recorder.RecordVisibleArea(centerX: -10, centerZ: 20, radius: 2);

        Assert.Equal(25, source.SampledColumns.Count);
        Assert.All(source.SampledColumns, point => Assert.InRange(point.X, -12, -8));
        Assert.All(source.SampledColumns, point => Assert.InRange(point.Z, 18, 22));
        Assert.Equal(25, source.SampledColumns.Distinct().Count());
    }

    [Fact]
    public void Negative_radius_is_rejected_before_terrain_is_sampled()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource();
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var recorder = new ExplorationRecorder(sampler, new ExplorationTileStore(directory.Path));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => recorder.RecordVisibleArea(centerX: 0, centerZ: 0, radius: -1));
        Assert.Empty(source.SampledColumns);
    }
}
