using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TerrainHeightShadingTests
{
    [Fact]
    public void Flat_ground_encodes_as_neutral_brightness()
    {
        Assert.Equal(
            TerrainHeightShading.Neutral,
            TerrainHeightShading.Calculate(64, 64, 64, 64, 64));
    }

    [Fact]
    public void Light_facing_slope_is_brighter_than_the_opposite_slope()
    {
        var facing = TerrainHeightShading.Calculate(60, 60, 68, 68, 64);
        var away = TerrainHeightShading.Calculate(68, 68, 60, 60, 64);

        Assert.True(facing > TerrainHeightShading.Neutral);
        Assert.True(away < TerrainHeightShading.Neutral);
    }

    [Fact]
    public void Enclosed_depression_is_darker_and_isolated_peak_is_brighter()
    {
        var depression = TerrainHeightShading.Calculate(70, 70, 70, 70, 64);
        var peak = TerrainHeightShading.Calculate(58, 58, 58, 58, 64);

        Assert.True(depression < TerrainHeightShading.Neutral);
        Assert.True(peak > TerrainHeightShading.Neutral);
    }

    [Fact]
    public void Renderer_applies_saved_height_shading_only_when_the_setting_is_enabled()
    {
        var source = new HeightShadePixelSource(
            new MapTerrainPixel(new Rgba32(120, 80, 40, 255), HeightShade: 64));
        var unshaded = new RecordingRenderSink();
        var shaded = new RecordingRenderSink();
        var transform = new MapTransform(Vector2.Zero, 1f, Vector2.One);

        TravelMapRenderModel.RenderTerrain(source, transform, 1f, unshaded, useHeightShading: false);
        TravelMapRenderModel.RenderTerrain(source, transform, 1f, shaded, useHeightShading: true);

        Assert.Equal(new Rgba32(120, 80, 40, 255), Assert.Single(unshaded.Terrain).Color);
        Assert.Equal(new Rgba32(60, 40, 20, 255), Assert.Single(shaded.Terrain).Color);
    }

    [Fact]
    public void Unknown_legacy_shading_is_rendered_neutrally()
    {
        var color = new Rgba32(90, 60, 30, 255);

        Assert.Equal(color, TerrainHeightShading.Apply(color, TerrainHeightShading.Unknown));
    }

    [Fact]
    public void Zoomed_out_tile_regions_average_known_height_shades_and_ignore_legacy_unknowns()
    {
        var tile = new MapTile(0, 0);
        tile.SetPixel(0, 0, new Rgba32(100, 100, 100, 255), heightShade: 64);
        tile.SetPixel(1, 0, new Rgba32(100, 100, 100, 255), heightShade: 192);
        tile.SetPixel(0, 1, new Rgba32(100, 100, 100, 255));
        tile.SetPixel(1, 1, new Rgba32(100, 100, 100, 255), heightShade: 128);

        var found = tile.CreateVersionedSnapshot().Snapshot.TryGetExploredTerrainRegion(
            0,
            0,
            2,
            2,
            out var pixel);

        Assert.True(found);
        Assert.Equal(new Rgba32(100, 100, 100, 255), pixel.Color);
        Assert.Equal(TerrainHeightShading.Neutral, pixel.HeightShade);
    }

    private sealed class HeightShadePixelSource(MapTerrainPixel pixel) : IExploredMapPixelSource
    {
        public IExploredMapReadSession BeginReadSession() => new Session(pixel);

        private sealed class Session(MapTerrainPixel pixel) : IExploredMapReadSession
        {
            public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
            {
                var found = TryGetExploredTerrainPixel(worldX, worldZ, out var terrainPixel);
                color = terrainPixel.Color;
                return found;
            }

            public bool TryGetExploredTerrainPixel(
                int worldX,
                int worldZ,
                out MapTerrainPixel terrainPixel)
            {
                var found = worldX == 0 && worldZ == 0;
                terrainPixel = found ? pixel : default;
                return found;
            }

            public void Dispose()
            {
            }
        }
    }
}
