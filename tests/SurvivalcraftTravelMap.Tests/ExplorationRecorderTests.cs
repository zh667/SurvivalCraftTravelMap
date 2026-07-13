using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class ExplorationRecorderTests
{
    [Fact]
    public void Recorder_reports_pressure_instead_of_growing_dirty_cache()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExplorationTileStore(directory.Path, capacity: 1);
        var sampler = new TerrainMapSampler(
            new FakeTerrainMapSource(defaultContent: 1),
            TerrainMapSamplerTests.CreatePixelData());
        var recorder = new ExplorationRecorder(sampler, store);

        var first = recorder.RecordVisibleArea(0, 0, radius: 0);
        var pressure = recorder.RecordVisibleArea(MapTile.Size * 10, 0, radius: 0);

        Assert.Equal(ExplorationRecordResult.Recorded, first);
        Assert.Equal(ExplorationRecordResult.Pressure, pressure);
        Assert.Equal(1, store.Diagnostics.CachedTileCount);
    }
    [Fact]
    public async Task Mutation_survives_concurrent_flush_and_trim_pressure_at_low_capacity()
    {
        using var directory = new TemporaryDirectory();
        using var source = new BlockingSecondSampleTerrainSource();
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path, capacity: 4);
        var recorder = new ExplorationRecorder(sampler, store);
        var recording = Task.Run(
            () => recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1),
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(source.SecondSampleStarted.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            await store.FlushAsync(TestContext.Current.CancellationToken);
            store.GetOrLoad(9, 9);
        }
        finally
        {
            source.AllowSecondSample.Set();
        }

        await recording.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloaded = new ExplorationTileStore(directory.Path);
        Assert.True(reloaded.GetOrLoad(0, 0).TryGetPixel(63, 62, out _));
        Assert.True(reloaded.GetOrLoad(1, 0).TryGetPixel(0, 62, out _));
    }

    [Fact]
    public async Task Sampler_exception_survives_concurrent_flush_and_trim_and_keeps_successful_pixels()
    {
        using var directory = new TemporaryDirectory();
        using var source = new BlockingSecondSampleTerrainSource(throwAtSample: 4);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path, capacity: 4);
        var recorder = new ExplorationRecorder(sampler, store);
        var recording = Task.Run(
            () => recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1),
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(source.SecondSampleStarted.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            await store.FlushAsync(TestContext.Current.CancellationToken);
            store.GetOrLoad(9, 9);
        }
        finally
        {
            source.AllowSecondSample.Set();
        }

        var exception = await Assert.ThrowsAsync<TerrainSamplingException>(
            () => recording.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("sample 4", exception.Message);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloaded = new ExplorationTileStore(directory.Path);
        var firstTile = reloaded.GetOrLoad(0, 0);
        Assert.True(firstTile.TryGetPixel(62, 62, out _));
        Assert.True(firstTile.TryGetPixel(63, 62, out _));
        Assert.True(reloaded.GetOrLoad(1, 0).TryGetPixel(0, 62, out _));
    }

    [Fact]
    public async Task Low_capacity_recording_pins_every_touched_tile_until_it_is_dirty()
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeTerrainMapSource(topHeight: 64, defaultContent: 1);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path, capacity: 4);
        var recorder = new ExplorationRecorder(sampler, store);

        recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["0_0.sctm", "0_1.sctm", "1_0.sctm", "1_1.sctm"],
            Directory.GetFiles(directory.Path, "*.sctm")
                .Select(path => Path.GetFileName(path)!)
                .Order()
                .ToArray());
    }

    [Fact]
    public async Task Low_capacity_sampler_failure_preserves_its_exception_and_every_successful_pixel()
    {
        using var directory = new TemporaryDirectory();
        var source = new ThrowingTerrainMapSource(throwAtSample: 4);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path, capacity: 4);
        var recorder = new ExplorationRecorder(sampler, store);

        var exception = Assert.Throws<TerrainSamplingException>(
            () => recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1));
        Assert.Equal("sample 4", exception.Message);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloadedStore = new ExplorationTileStore(directory.Path);
        var firstTile = reloadedStore.GetOrLoad(0, 0);
        Assert.True(firstTile.TryGetPixel(62, 62, out _));
        Assert.True(firstTile.TryGetPixel(63, 62, out _));
        Assert.True(reloadedStore.GetOrLoad(1, 0).TryGetPixel(0, 62, out _));
    }

    [Fact]
    public async Task Flush_interleaved_with_recording_does_not_clear_later_pixel_writes()
    {
        using var directory = new TemporaryDirectory();
        using var source = new BlockingSecondSampleTerrainSource();
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path);
        var alreadyDirty = store.GetOrLoad(0, 0);
        alreadyDirty.SetPixel(10, 10, new Rgba32(9, 9, 9, 255));
        store.MarkDirty(alreadyDirty);
        var recorder = new ExplorationRecorder(sampler, store);
        var recording = Task.Run(
            () => recorder.RecordVisibleArea(centerX: 1, centerZ: 1, radius: 1),
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(source.SecondSampleStarted.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            await store.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            source.AllowSecondSample.Set();
        }

        await recording.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloaded = new ExplorationTileStore(directory.Path).GetOrLoad(0, 0);
        for (var z = 0; z <= 2; z++)
        {
            for (var x = 0; x <= 2; x++)
            {
                Assert.True(reloaded.TryGetPixel(x, z, out _), $"Expected persisted pixel ({x}, {z}).");
            }
        }
    }

    [Fact]
    public async Task Flush_interleaving_followed_by_sampler_failure_keeps_each_written_tile_dirty()
    {
        using var directory = new TemporaryDirectory();
        using var source = new BlockingSecondSampleTerrainSource(throwAtSample: 4);
        var sampler = new TerrainMapSampler(source, TerrainMapSamplerTests.CreatePixelData());
        var store = new ExplorationTileStore(directory.Path);
        var alreadyDirty = store.GetOrLoad(0, 0);
        alreadyDirty.SetPixel(10, 10, new Rgba32(9, 9, 9, 255));
        store.MarkDirty(alreadyDirty);
        var recorder = new ExplorationRecorder(sampler, store);
        var recording = Task.Run(
            () => recorder.RecordVisibleArea(centerX: 63, centerZ: 63, radius: 1),
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(source.SecondSampleStarted.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            await store.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            source.AllowSecondSample.Set();
        }

        await Assert.ThrowsAsync<TerrainSamplingException>(
            () => recording.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var reloadedStore = new ExplorationTileStore(directory.Path);
        var firstTile = reloadedStore.GetOrLoad(0, 0);
        Assert.True(firstTile.TryGetPixel(62, 62, out _));
        Assert.True(firstTile.TryGetPixel(63, 62, out _));
        Assert.True(reloadedStore.GetOrLoad(1, 0).TryGetPixel(0, 62, out _));
    }

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

internal sealed class BlockingSecondSampleTerrainSource(int? throwAtSample = null)
    : ITerrainMapSource, IDisposable
{
    private int _sampleCount;

    internal ManualResetEventSlim SecondSampleStarted { get; } = new(initialState: false);

    internal ManualResetEventSlim AllowSecondSample { get; } = new(initialState: false);

    public int GetTopHeight(int x, int z)
    {
        var sample = Interlocked.Increment(ref _sampleCount);
        if (sample == 2)
        {
            SecondSampleStarted.Set();
            if (!AllowSecondSample.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the blocked terrain sample.");
            }
        }

        if (sample == throwAtSample)
        {
            throw new TerrainSamplingException($"sample {sample}");
        }

        return 64;
    }

    public int GetContent(int x, int y, int z) => 1;

    public int GetSeasonalTemperature(int x, int z) => 8;

    public int GetSeasonalHumidity(int x, int z) => 8;

    public void Dispose()
    {
        AllowSecondSample.Dispose();
        SecondSampleStarted.Dispose();
    }
}

internal sealed class ThrowingTerrainMapSource(int throwAtSample) : ITerrainMapSource
{
    private int _sampleCount;

    public int GetTopHeight(int x, int z)
    {
        var sample = ++_sampleCount;
        if (sample == throwAtSample)
        {
            throw new TerrainSamplingException($"sample {sample}");
        }

        return 64;
    }

    public int GetContent(int x, int y, int z) => 1;

    public int GetSeasonalTemperature(int x, int z) => 8;

    public int GetSeasonalHumidity(int x, int z) => 8;
}

internal sealed class TerrainSamplingException(string message) : Exception(message);
