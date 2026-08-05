using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MiniMapTerrainTextureTests
{
    private static MapTransform CreateTransform(
        float viewportSize = 40f,
        float blocksPerPixel = 1f,
        Vector2 center = default,
        float rotation = 0f) => new(
        center,
        blocksPerPixel,
        new Vector2(viewportSize),
        rotation);

    private static void FillCompletely(
        MiniMapTerrainTextureModel model,
        IExploredMapPixelSource source,
        MapTransform transform,
        ref double time)
    {
        for (var iteration = 0; iteration < 64 && model.DirtyCellCount > 0; iteration++)
        {
            time += 1.0;
            model.Update(source, transform, 0f, time);
        }

        Assert.Equal(0, model.DirtyCellCount);
    }

    [Fact]
    public void Resolution_covers_rotating_viewport_with_margin()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();

        model.Update(source, CreateTransform(viewportSize: 200f, blocksPerPixel: 1f), 0f, 0.0);

        // Needed span: 200 * sqrt(2) * 1.3 ≈ 368 blocks for the rotating viewport plus margin.
        Assert.True(model.SpanBlocks >= 368);
        Assert.Equal(1, model.TexelBlocks);
        Assert.Equal(512, model.TextureSize);
    }

    [Fact]
    public void Zoomed_out_view_downsamples_instead_of_growing_texture()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();

        model.Update(source, CreateTransform(viewportSize: 200f, blocksPerPixel: 4f), 0f, 0.0);

        Assert.True(model.SpanBlocks >= 1471);
        Assert.True(model.TextureSize <= MiniMapTerrainTextureModel.MaxTextureSize);
        Assert.True(model.TexelBlocks > 1);
    }

    [Fact]
    public void First_update_requests_upload_and_bakes_explored_pixels()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource
        {
            Color = new Rgba32(10, 200, 30, 255),
        };
        var transform = CreateTransform();

        var uploaded = model.Update(source, transform, 0f, 0.0);

        Assert.True(uploaded);
        Assert.Equal(new Rgba32(10, 200, 30, 255), GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void Unexplored_texels_stay_transparent()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource
        {
            Explored = false,
        };
        var transform = CreateTransform();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        FillCompletely(model, source, transform, ref time);

        Assert.Equal(default, GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void Refill_is_budgeted_per_frame_and_converges()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        // 200px viewport => 512 texture => 64 cells; a reset frame may fill at most 16.
        var transform = CreateTransform(viewportSize: 200f);

        model.Update(source, transform, 0f, 0.0);
        var remaining = model.DirtyCellCount;
        Assert.True(remaining >= 64 - (MiniMapTerrainTextureModel.ResetRefillTexelBudget / 4096));
        Assert.True(remaining < 64);

        var time = 0.0;
        FillCompletely(model, source, transform, ref time);
    }

    [Fact]
    public void Center_cells_are_refilled_before_edge_cells()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform(viewportSize: 200f);

        model.Update(source, transform, 0f, 0.0);

        // After the reset frame the player-centered texel must already be filled even though
        // most cells are still pending.
        Assert.True(model.DirtyCellCount > 0);
        Assert.Equal(source.Color, GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void Tile_mutation_invalidates_only_after_poll_interval_and_reuploads()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        FillCompletely(model, source, transform, ref time);
        Assert.False(model.Update(source, transform, 0f, time));

        source.Color = new Rgba32(200, 10, 10, 255);
        source.BumpVersion();

        // Same-frame update: version poll is throttled, nothing refreshes yet.
        Assert.False(model.Update(source, transform, 0f, time));

        time += MiniMapTerrainTextureModel.VersionPollIntervalSeconds + 0.01;
        var uploaded = false;
        for (var iteration = 0; iteration < 8 && !uploaded; iteration++)
        {
            uploaded = model.Update(source, transform, 0f, time);
            time += MiniMapTerrainTextureModel.UploadMinIntervalSeconds + 0.01;
        }

        Assert.True(uploaded);
        var expectedTime = time;
        FillCompletely(model, source, transform, ref expectedTime);
        Assert.Equal(new Rgba32(200, 10, 10, 255), GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void Unchanged_world_never_requests_further_uploads()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        FillCompletely(model, source, transform, ref time);

        for (var frame = 0; frame < 20; frame++)
        {
            time += 1.0;
            Assert.False(model.Update(source, transform, 0f, time));
        }
    }

    [Fact]
    public void Scroll_keeps_retained_cells_without_rereading_them()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        FillCompletely(model, source, transform, ref time);
        var readsAfterFill = source.PixelReads;
        var spanBlocks = model.SpanBlocks;

        // Move far enough to force a recenter but keep most of the domain overlapping.
        var moved = CreateTransform(center: new Vector2(spanBlocks / 4f, 0f));
        time += 1.0;
        model.Update(source, moved, 0f, time);
        FillCompletely(model, source, moved, ref time);

        Assert.Equal(new Rgba32(60, 120, 60, 255), GetPixelAtWorld(model, 0, 0));
        var refilledTexels = source.PixelReads - readsAfterFill;
        var totalTexels = model.TextureSize * (long)model.TextureSize;
        Assert.True(
            refilledTexels < totalTexels,
            $"scroll re-read {refilledTexels} of {totalTexels} texels");
    }

    [Fact]
    public void Device_reset_invalidation_forces_one_reupload()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        FillCompletely(model, source, transform, ref time);

        model.InvalidateUpload();
        time += 0.001;
        Assert.True(model.Update(source, transform, 0f, time));
        time += 1.0;
        Assert.False(model.Update(source, transform, 0f, time));
    }

    [Fact]
    public void Shading_strength_change_triggers_full_rebake()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource
        {
            HeightShade = TerrainHeightShading.Encode(0.5f),
        };
        var transform = CreateTransform();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        FillCompletely(model, source, transform, ref time);
        var flat = GetPixelAtWorld(model, 0, 0);
        Assert.Equal(source.Color, flat);

        time += 1.0;
        model.Update(source, transform, 1f, time);
        FillCompletely(model, source, transform, ref time);
        var shaded = GetPixelAtWorld(model, 0, 0);
        Assert.True(shaded.R < flat.R);
    }

    [Fact]
    public void Quad_builder_maps_screen_center_to_player_world_uv()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var center = new Vector2(1000f, -500f);
        var transform = CreateTransform(center: center);
        model.Update(source, transform, 0f, 0.0);

        var geometry = MapShapeGeometry.Create(transform.ViewportSize, MapShape.Square);
        var triangles = MiniMapTerrainTextureQuad.BuildTriangles(
            transform,
            geometry,
            model.OriginX,
            model.OriginZ,
            model.SpanBlocks);

        Assert.True(triangles.Count >= 3);
        Assert.Equal(0, triangles.Count % 3);

        // Every produced vertex must map back to the world position its UV claims.
        foreach (var vertex in triangles)
        {
            var world = transform.ScreenToWorld(vertex.Position);
            var expectedU = (world.X - model.OriginX) / model.SpanBlocks;
            var expectedV = (world.Y - model.OriginZ) / model.SpanBlocks;
            Assert.Equal(expectedU, vertex.TexCoord.X, 3);
            Assert.Equal(expectedV, vertex.TexCoord.Y, 3);
            Assert.InRange(vertex.TexCoord.X, -0.001f, 1.001f);
            Assert.InRange(vertex.TexCoord.Y, -0.001f, 1.001f);
        }
    }

    [Fact]
    public void Quad_builder_clips_to_circular_shape()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform(viewportSize: 100f);
        model.Update(source, transform, 0f, 0.0);

        var geometry = MapShapeGeometry.Create(transform.ViewportSize, MapShape.Circle);
        var triangles = MiniMapTerrainTextureQuad.BuildTriangles(
            transform,
            geometry,
            model.OriginX,
            model.OriginZ,
            model.SpanBlocks);

        Assert.True(triangles.Count >= 3);
        foreach (var vertex in triangles)
        {
            var offset = vertex.Position - new Vector2(50f);
            Assert.True(offset.Length() <= 50.5f, $"vertex {vertex.Position} escapes the circle");
        }
    }

    [Fact]
    public void Rotated_transform_round_trips_uvs()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform(rotation: 0.7f);
        model.Update(source, transform, 0f, 0.0);

        var geometry = MapShapeGeometry.Create(transform.ViewportSize, MapShape.Circle);
        var triangles = MiniMapTerrainTextureQuad.BuildTriangles(
            transform,
            geometry,
            model.OriginX,
            model.OriginZ,
            model.SpanBlocks);

        Assert.True(triangles.Count >= 3);
        foreach (var vertex in triangles)
        {
            Assert.InRange(vertex.TexCoord.X, -0.001f, 1.001f);
            Assert.InRange(vertex.TexCoord.Y, -0.001f, 1.001f);
        }
    }

    private static Rgba32 GetPixelAtWorld(MiniMapTerrainTextureModel model, long worldX, long worldZ)
    {
        var texelX = (int)((worldX - model.OriginX) / model.TexelBlocks);
        var texelZ = (int)((worldZ - model.OriginZ) / model.TexelBlocks);
        Assert.InRange(texelX, 0, model.TextureSize - 1);
        Assert.InRange(texelZ, 0, model.TextureSize - 1);
        return model.Pixels[(texelZ * model.TextureSize) + texelX];
    }
}

/// <summary>
/// Uniform in-memory pixel source with per-tile version stamps and read accounting. Aggregate
/// region reads count as one read per covered texel request.
/// </summary>
internal sealed class TexturePixelSource :
    IExploredMapPixelSource,
    IExploredMapTileVersionSource
{
    private long _version = 1;

    public bool Explored { get; set; } = true;

    public Rgba32 Color { get; set; } = new(60, 120, 60, 255);

    public byte HeightShade { get; set; } = TerrainHeightShading.Neutral;

    public long PixelReads { get; private set; }

    public long MutationVersion => _version;

    public long GetTileMutationVersion(int tileX, int tileZ) => _version;

    public void BumpVersion() => _version++;

    public IExploredMapReadSession BeginReadSession() => new Session(this);

    private sealed class Session(TexturePixelSource owner) :
        IExploredMapReadSession,
        IExploredMapAggregateReadSession
    {
        public bool TryGetExploredPixel(int worldX, int worldZ, out Rgba32 color)
        {
            var found = TryGetExploredTerrainPixel(worldX, worldZ, out var pixel);
            color = pixel.Color;
            return found;
        }

        public bool TryGetExploredTerrainPixel(int worldX, int worldZ, out MapTerrainPixel pixel)
        {
            owner.PixelReads++;
            pixel = owner.Explored
                ? new MapTerrainPixel(owner.Color, owner.HeightShade)
                : default;
            return owner.Explored;
        }

        public bool TryGetExploredRegion(int worldX, int worldZ, int width, int height, out Rgba32 color)
        {
            var found = TryGetExploredTerrainRegion(worldX, worldZ, width, height, out var pixel);
            color = pixel.Color;
            return found;
        }

        public bool TryGetExploredTerrainRegion(
            int worldX,
            int worldZ,
            int width,
            int height,
            out MapTerrainPixel pixel) => TryGetExploredTerrainPixel(worldX, worldZ, out pixel);

        public void Dispose()
        {
        }
    }
}
