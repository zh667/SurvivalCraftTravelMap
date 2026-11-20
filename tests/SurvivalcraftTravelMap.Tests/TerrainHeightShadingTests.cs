using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;
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
        // Light comes from world +X / +Z (screen top-left): a slope that rises toward -X / -Z
        // faces it.
        var facing = TerrainHeightShading.Calculate(68, 68, 60, 60, 64);
        var away = TerrainHeightShading.Calculate(60, 60, 68, 68, 64);

        Assert.True(facing > TerrainHeightShading.Neutral);
        Assert.True(away < TerrainHeightShading.Neutral);
    }

    // The map plane is rotated 180 degrees from raw world (X, Z) (see MapTransform), so the
    // light direction must be judged on screen, not in world coordinates: people read relief
    // as lit from the screen's top-left, and the 2.8.0 shading lit it from the bottom-right,
    // which made pits look like mounds (player report 2026-11-19).
    [Fact]
    public void Raised_block_is_lit_from_the_screen_top_left_in_north_up_view()
    {
        var shades = ShadeOnScreenAroundSquare(heightDelta: 3);

        var left = shades[(8, 10)];
        var right = shades[(12, 10)];
        var top = shades[(10, 8)];
        var bottom = shades[(10, 12)];

        Assert.True(left > right, $"screen-left neighbour {left} should be brighter than screen-right {right}");
        Assert.True(top > bottom, $"screen-top neighbour {top} should be brighter than screen-bottom {bottom}");
        Assert.True(left > TerrainHeightShading.Neutral);
        Assert.True(bottom < TerrainHeightShading.Neutral);
    }

    [Fact]
    public void Pit_inner_wall_on_the_screen_top_left_is_darker_in_north_up_view()
    {
        var shades = ShadeOnScreenAroundSquare(heightDelta: -3);

        var leftWall = shades[(9, 10)];
        var rightWall = shades[(11, 10)];
        var topWall = shades[(10, 9)];
        var bottomWall = shades[(10, 11)];

        Assert.True(leftWall < rightWall, $"screen-left inner wall {leftWall} should be darker than screen-right {rightWall}");
        Assert.True(topWall < bottomWall, $"screen-top inner wall {topWall} should be darker than screen-bottom {bottomWall}");
    }

    /// <summary>
    /// Flat ground at 64 with a 3x3 square (world cells 0..2) raised or lowered by
    /// <paramref name="heightDelta"/>. Each cell is shaded exactly like TerrainMapSampler does
    /// (west = x-1, north = z-1, east = x+1, south = z+1) and keyed by its north-up screen
    /// position. The square's centre lands on screen (10, 10), one block per pixel.
    /// </summary>
    private static Dictionary<(int X, int Y), byte> ShadeOnScreenAroundSquare(int heightDelta)
    {
        int Height(int x, int z) => x is >= 0 and < 3 && z is >= 0 and < 3 ? 64 + heightDelta : 64;

        var transform = new MapTransform(new Vector2(1.5f, 1.5f), 1f, new Vector2(20f, 20f));
        var result = new Dictionary<(int X, int Y), byte>();
        for (var z = -3; z <= 5; z++)
        {
            for (var x = -3; x <= 5; x++)
            {
                var shade = TerrainHeightShading.Calculate(
                    Height(x - 1, z),
                    Height(x, z - 1),
                    Height(x + 1, z),
                    Height(x, z + 1),
                    Height(x, z));
                var screen = transform.WorldToScreen(new Vector2(x + 0.5f, z + 0.5f));
                result[((int)MathF.Round(screen.X), (int)MathF.Round(screen.Y))] = shade;
            }
        }

        return result;
    }

    [Fact]
    public void Enclosed_depression_is_darker_and_isolated_peak_is_brighter()
    {
        var depression = TerrainHeightShading.Calculate(70, 70, 70, 70, 64);
        var peak = TerrainHeightShading.Calculate(58, 58, 58, 58, 64);

        Assert.True(depression < TerrainHeightShading.Neutral);
        Assert.True(peak > TerrainHeightShading.Neutral);
    }

    [Theory]
    [InlineData(0f, 120, 80, 40)] // Off: relief flattened, colour untouched.
    [InlineData(0.6f, 84, 56, 28)] // Soft: 0.7x of the baked deviation.
    [InlineData(1f, 60, 40, 20)] // Standard: the baked shade as-is (0.5x).
    [InlineData(1.7f, 18, 12, 6)] // High contrast: relief exaggerated (0.15x).
    public void Renderer_scales_saved_height_shading_by_the_selected_strength(
        float strength,
        byte red,
        byte green,
        byte blue)
    {
        var source = new HeightShadePixelSource(
            new MapTerrainPixel(new Rgba32(120, 80, 40, 255), HeightShade: 64));
        var sink = new RecordingRenderSink();
        var transform = new MapTransform(Vector2.Zero, 1f, Vector2.One);

        TravelMapRenderModel.RenderTerrain(source, transform, 1f, sink, heightShadingStrength: strength);

        Assert.Equal(new Rgba32(red, green, blue, 255), Assert.Single(sink.Terrain).Color);
    }

    [Theory]
    [InlineData(HeightShadingStyle.Off, 0f)]
    [InlineData(HeightShadingStyle.Soft, 0.6f)]
    [InlineData(HeightShadingStyle.Standard, 1f)]
    [InlineData(HeightShadingStyle.HighContrast, 1.7f)]
    public void Height_shading_styles_map_to_render_strengths(HeightShadingStyle style, float expected)
    {
        Assert.Equal(expected, style.ToStrength());
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
