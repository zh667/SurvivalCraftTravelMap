using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MapViewPixelSourceVersionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mvps").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void WriteTile(ExplorationTileStore store, int tileX, int tileZ)
    {
        using var lease = store.AcquireMutation(tileX, tileZ);
        for (var z = 0; z < 64; z++)
        {
            for (var x = 0; x < 64; x++)
            {
                lease.Tile.SetPixel(x, z, new Rgba32(60, 140, 60, 255), 128);
            }
        }
    }

    [Fact]
    public void Wrapper_forwards_the_active_sources_version_stamps()
    {
        var store = new ExplorationTileStore(_directory, capacity: 16);
        var wrapper = new MapViewPixelSource(
            new TileStoreMapPixelSource(store),
            () => MapViewMode.Surface,
            () => throw new InvalidOperationException("cave source must not be used"));

        var versioned = Assert.IsAssignableFrom<IExploredMapTileVersionSource>(wrapper);
        var globalBefore = versioned.MutationVersion;
        var tileBefore = versioned.GetTileMutationVersion(1, 0);

        WriteTile(store, 1, 0);

        Assert.NotEqual(globalBefore, versioned.MutationVersion);
        Assert.NotEqual(tileBefore, versioned.GetTileMutationVersion(1, 0));
        Assert.Equal(versioned.GetTileMutationVersion(2, 5), versioned.GetTileMutationVersion(2, 5));
    }

    [Fact]
    public void Switching_view_sources_changes_every_version_stamp()
    {
        var surfaceStore = new ExplorationTileStore(Path.Combine(_directory, "s"), capacity: 16);
        var caveStore = new ExplorationTileStore(Path.Combine(_directory, "c"), capacity: 16);
        var caveSource = new TileStoreMapPixelSource(caveStore);
        var mode = MapViewMode.Surface;
        var wrapper = new MapViewPixelSource(
            new TileStoreMapPixelSource(surfaceStore),
            () => mode,
            () => caveSource);
        var versioned = (IExploredMapTileVersionSource)wrapper;

        var surfaceStamp = versioned.GetTileMutationVersion(0, 0);
        mode = MapViewMode.Cave;
        Assert.NotEqual(surfaceStamp, versioned.GetTileMutationVersion(0, 0));
    }

    [Fact]
    public void Exploration_recorded_while_standing_still_reaches_the_minimap_texture()
    {
        // Regression: the mini map receives MapViewPixelSource, not the raw tile-store source.
        // Before the wrapper forwarded version stamps, the texture cache treated it as immutable
        // and newly recorded exploration only appeared after a scroll or a rejoin.
        var store = new ExplorationTileStore(_directory, capacity: 64);
        var wrapper = new MapViewPixelSource(
            new TileStoreMapPixelSource(store),
            () => MapViewMode.Surface,
            () => throw new InvalidOperationException("cave source must not be used"));
        var model = new MiniMapTerrainTextureModel();
        var transform = new MapTransform(new Vector2(117f, 9f), 1f, new Vector2(240f, 240f));
        var time = 0.0;

        void RunFrames(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                time += 1.0 / 60.0;
                model.Update(wrapper, transform, 0.5f, time);
            }
        }

        int Opaque() => model.Pixels.Count(pixel => pixel.A != 0);

        RunFrames(60);
        Assert.Equal(0, Opaque());

        WriteTile(store, 1, 0);
        WriteTile(store, 0, 0);
        RunFrames(60);

        Assert.True(Opaque() > 0, "recorded exploration must reach the texture without a scroll");
    }
}
