using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Large-map model with a frozen refill clock: the wall-clock budget never binds, so these
    /// tests observe the deterministic texel cost model instead of how fast the machine is.
    /// </summary>
    private static MiniMapTerrainTextureModel CreateLargeMapModel(
        LargeMapDetail detail = LargeMapDetail.Standard,
        MiniMapTerrainTextureOptions? options = null) =>
        new(options ?? MiniMapTerrainTextureOptions.ForLargeMap(detail))
        {
            RefillTimestampProvider = static () => 0L,

            // Runs the real background code path, but inline, so a batch is always finished by the
            // time Update returns and the fill stays frame-by-frame deterministic.
            FillScheduler = static work =>
            {
                work();
                return Task.CompletedTask;
            },
        };

    private static void FillCompletely(
        MiniMapTerrainTextureModel model,
        IExploredMapPixelSource source,
        MapTransform transform,
        ref double time,
        float heightShadingStrength = 0f)
    {
        // The strength has to match what the caller last drove the model with: a different one is
        // a rebake, which would reset the very fill this is waiting on.
        for (var iteration = 0; iteration < 256 && model.DirtyCellCount > 0; iteration++)
        {
            time += 1.0;
            model.Update(source, transform, heightShadingStrength, time);
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
        FillCompletely(model, source, transform, ref time, heightShadingStrength: 1f);
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

    [Fact]
    public void Large_map_options_resolve_one_texel_per_screen_pixel_at_the_default_zoom()
    {
        // A phone lays out the UI at ~850 units wide whatever its physical resolution, and the
        // large map defaults to 2 blocks per unit. One texel per unit is the whole point of the
        // Standard tier, so guard it.
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(850f, 478f));
        var model = CreateLargeMapModel();

        model.Update(new TexturePixelSource(), transform, 0f, 0.0);

        Assert.Equal(2, model.TexelBlocks);
        Assert.Equal(1_024, model.TextureSize);
        Assert.True(
            model.SpanBlocks >= 850 * 2,
            $"domain spans {model.SpanBlocks} blocks, short of the 1700-block viewport");
    }

    [Fact]
    public void Not_covering_rotation_buys_a_sharper_texel_than_the_mini_map_settings_would()
    {
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(850f, 478f));
        var options = MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard);
        var unrotated = CreateLargeMapModel(options: options);
        var rotating = CreateLargeMapModel(options: options with
        {
            CoversRotatingViewport = true,
            DomainMarginFactor = MiniMapTerrainTextureModel.DomainMarginFactor,
        });

        unrotated.Update(new TexturePixelSource(), transform, 0f, 0.0);
        rotating.Update(new TexturePixelSource(), transform, 0f, 0.0);

        Assert.True(unrotated.TexelBlocks < rotating.TexelBlocks);
    }

    [Fact]
    public void Coarse_prepass_paints_every_cell_before_refining_any()
    {
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(850f, 478f));
        var source = new TexturePixelSource();
        var model = CreateLargeMapModel();

        var time = 0.0;
        model.Update(source, transform, 0f, time);
        for (var frame = 0; frame < 256 && model.EmptyCellCount > 0; frame++)
        {
            time += 1.0;
            model.Update(source, transform, 0f, time);
        }

        // Everything is painted long before everything is sharp: that is what stops the map from
        // showing up as a mostly empty screen while a large buffer fills.
        Assert.Equal(0, model.EmptyCellCount);
        Assert.True(model.DirtyCellCount > 0);
        Assert.Equal(source.Color, GetPixelAtWorld(model, 0, 0));

        var farCornerX = model.OriginX + model.SpanBlocks - model.TexelBlocks;
        Assert.Equal(source.Color, GetPixelAtWorld(model, farCornerX, model.OriginZ));
    }

    [Fact]
    public void Mini_map_keeps_filling_at_full_detail_without_a_prepass()
    {
        var model = new MiniMapTerrainTextureModel();
        var source = new TexturePixelSource();
        var transform = CreateTransform(viewportSize: 200f);

        model.Update(source, transform, 0f, 0.0);

        // No prepass means a painted cell is a finished cell.
        Assert.Equal(model.DirtyCellCount, model.EmptyCellCount);
    }

    [Fact]
    public void Invalidating_content_repaints_from_scratch()
    {
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(850f, 478f));
        var source = new TexturePixelSource();
        var model = CreateLargeMapModel();
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        Assert.NotEqual(default, GetPixelAtWorld(model, 0, 0));

        // A cave/surface switch reuses the same tiles with unrelated content, so stale pixels have
        // to go rather than linger until the budgeted refill reaches them.
        source.Color = new Rgba32(9, 9, 9, 255);
        model.InvalidateContent();

        Assert.Equal(default, GetPixelAtWorld(model, 0, 0));
        Assert.Equal(model.DirtyCellCount, model.EmptyCellCount);

        // The cleared buffer is pushed to the GPU first, so the previous layer leaves the screen
        // straight away; painting the new one starts on the frame after.
        time += 1.0;
        Assert.True(model.Update(source, transform, 0f, time));
        Assert.Equal(default, GetPixelAtWorld(model, 0, 0));

        time += 1.0;
        model.Update(source, transform, 0f, time);
        Assert.Equal(new Rgba32(9, 9, 9, 255), GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void Texel_never_aggregates_more_than_one_map_tile()
    {
        // Region reads may not straddle a tile, so a texel wider than a tile reads nothing at all
        // — the domain has to grow instead of coarsening further.
        var source = new TexturePixelSource();
        foreach (var detail in Enum.GetValues<LargeMapDetail>())
        {
            var model = CreateLargeMapModel(detail);
            var transform = new MapTransform(Vector2.Zero, 32f, new Vector2(1700f, 956f));

            model.Update(source, transform, 0f, 0.0);

            Assert.InRange(model.TexelBlocks, 1, MapTile.Size);
        }
    }

    [Fact]
    public void Zoomed_out_large_map_stops_polling_versions_instead_of_stamping_every_tile()
    {
        var source = new CountingVersionSource();
        var options = MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard);
        var model = CreateLargeMapModel(options: options);
        var transform = new MapTransform(Vector2.Zero, 32f, new Vector2(1700f, 956f));
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        var stampsAfterFill = source.TileVersionQueries;
        source.BumpVersion();

        time += options.VersionPollIntervalSeconds + 0.01;
        model.Update(source, transform, 0f, time);

        var cells = model.TextureSize / model.CellTexelsPerSide;
        var unboundedScan = (long)cells * cells * model.TilesPerCell;
        Assert.True(
            unboundedScan > options.MaxVersionScanTilesPerPoll,
            "test no longer exercises the zoomed-out guard");
        Assert.True(
            source.TileVersionQueries - stampsAfterFill < unboundedScan,
            "version poll stamped every covered tile");
    }

    [Fact]
    public void Zooming_out_carries_the_previous_terrain_into_the_rebuilt_buffer()
    {
        var source = new TexturePixelSource();
        var model = CreateLargeMapModel();
        var zoomedIn = new MapTransform(Vector2.Zero, 1f, new Vector2(850f, 478f));
        var time = 0.0;

        model.Update(source, zoomedIn, 0f, time);
        for (var frame = 0; frame < 256 && model.EmptyCellCount > 0; frame++)
        {
            time += 1.0;
            model.Update(source, zoomedIn, 0f, time);
        }

        Assert.Equal(0, model.EmptyCellCount);
        var carried = source.Color;
        var texelBlocksBefore = model.TexelBlocks;

        // Zoom out far enough that the old domain cannot cover the viewport: the rebuild is not
        // deferred, and it must arrive with the previous terrain minified into it rather than with
        // a blank buffer that then has to be repainted from scratch.
        source.Color = new Rgba32(1, 2, 3, 255);
        var zoomedOut = new MapTransform(Vector2.Zero, 4f, new Vector2(850f, 478f));
        time += 1.0;
        model.Update(source, zoomedOut, 0f, time);

        Assert.True(model.TexelBlocks > texelBlocksBefore);
        Assert.Equal(carried, GetPixelAtWorld(model, 0, 0));
        Assert.Equal(carried, GetPixelAtWorld(model, 400, 400));
        Assert.True(
            model.EmptyCellCount < model.DirtyCellCount,
            "the rebuilt buffer blanked every cell instead of carrying the previous terrain over");

        // Carried-over pixels are a stand-in, not the answer: the refill still replaces them.
        for (var frame = 0; frame < 512 && GetPixelAtWorld(model, 0, 0) == carried; frame++)
        {
            time += 1.0;
            model.Update(source, zoomedOut, 0f, time);
        }

        Assert.Equal(new Rgba32(1, 2, 3, 255), GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void Refining_the_resolution_waits_for_the_zoom_to_settle()
    {
        var source = new TexturePixelSource();
        var options = MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard);
        var model = CreateLargeMapModel(options: options);
        var zoomedOut = new MapTransform(Vector2.Zero, 4f, new Vector2(850f, 478f));
        var time = 0.0;

        model.Update(source, zoomedOut, 0f, time);
        var coarse = model.TexelBlocks;
        Assert.True(coarse > 1);

        // Mid-pinch: the current buffer still covers this viewport, so rebuilding for the finer
        // texel can wait — otherwise one gesture rebuilds at every size it sweeps through.
        var zoomedIn = new MapTransform(Vector2.Zero, 1f, new Vector2(850f, 478f));
        time += options.ResolutionSettleSeconds / 2.0;
        model.Update(source, zoomedIn, 0f, time);
        Assert.Equal(coarse, model.TexelBlocks);

        time += options.ResolutionSettleSeconds;
        model.Update(source, zoomedIn, 0f, time);
        Assert.True(model.TexelBlocks < coarse);
    }

    [Fact]
    public void A_domain_that_stopped_covering_the_viewport_is_rebuilt_without_waiting()
    {
        var source = new TexturePixelSource();
        var options = MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard);
        var model = CreateLargeMapModel(options: options);
        var zoomedIn = new MapTransform(Vector2.Zero, 1f, new Vector2(850f, 478f));
        var time = 0.0;

        model.Update(source, zoomedIn, 0f, time);
        var spanBefore = model.SpanBlocks;

        var zoomedOut = new MapTransform(Vector2.Zero, 4f, new Vector2(850f, 478f));
        time += options.ResolutionSettleSeconds / 10.0;
        model.Update(source, zoomedOut, 0f, time);

        Assert.True(
            model.SpanBlocks > spanBefore && model.SpanBlocks >= 850 * 4,
            $"domain still spans {model.SpanBlocks} blocks, short of the 3400-block viewport");
    }

    [Fact]
    public void Mini_map_resolution_changes_take_effect_on_the_frame_they_appear()
    {
        // The mini map zooms in discrete setting steps, so it has nothing to debounce.
        var source = new TexturePixelSource();
        var model = new MiniMapTerrainTextureModel();
        var transform = CreateTransform(viewportSize: 200f, blocksPerPixel: 1f);
        var time = 0.0;

        model.Update(source, transform, 0f, time);
        Assert.Equal(1, model.TexelBlocks);

        time += 0.001;
        model.Update(source, CreateTransform(viewportSize: 200f, blocksPerPixel: 4f), 0f, time);
        Assert.True(model.TexelBlocks > 1);
    }

    [Fact]
    public void A_slow_frame_stops_refilling_when_its_time_budget_runs_out()
    {
        // The texel cost model is calibrated for cold tiles, so on its own it starves the refill
        // once tiles are warm — the wall-clock budget is what lets a frame keep going. It has to
        // stop when the frame is actually spent, whatever the texel ceiling still allows.
        var source = new TexturePixelSource();
        var options = MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard);
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(850f, 478f));

        var unhurried = CreateLargeMapModel(options: options);
        unhurried.Update(source, transform, 0f, 0.0);

        var reads = 0L;
        var third = (long)(options.ResetRefillTimeBudgetSeconds * Stopwatch.Frequency / 3);
        var slow = new MiniMapTerrainTextureModel(options)
        {
            RefillTimestampProvider = () => reads++ * third,
            FillScheduler = static work =>
            {
                work();
                return Task.CompletedTask;
            },
        };
        slow.Update(source, transform, 0f, 0.0);

        Assert.True(
            slow.EmptyCellCount > unhurried.EmptyCellCount,
            $"the timed-out frame painted down to {slow.EmptyCellCount} empty cells, "
                + $"no better than the unhurried {unhurried.EmptyCellCount}");
        Assert.True(slow.EmptyCellCount < slow.DirtyCellCount, "the timed-out frame painted nothing");
    }

    [Fact]
    public void A_real_background_fill_converges_and_never_reports_an_upload_mid_batch()
    {
        // The production configuration, threads and all: the frame must never claim an upload
        // while a batch still owns the buffer, and the fill must still reach a sharp map.
        var source = new TexturePixelSource();
        var transform = new MapTransform(Vector2.Zero, 2f, new Vector2(850f, 478f));
        var model = new MiniMapTerrainTextureModel(
            MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard));
        Assert.True(model.Options.FillsInBackground);

        var time = 0.0;
        var uploads = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            time += 1.0 / 60.0;
            if (model.Update(source, transform, 0f, time))
            {
                uploads++;

                // Reading the buffer is only ever safe on a frame that asked for an upload.
                Assert.Equal(model.TextureSize * model.TextureSize, model.Pixels.Length);
            }

            if (model.DirtyCellCount == 0)
            {
                break;
            }

            Thread.Sleep(1);
        }

        Assert.Equal(0, model.DirtyCellCount);
        Assert.True(uploads > 0, "the map was never handed to the GPU");
        Assert.Equal(source.Color, GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void A_deferred_rebuild_keeps_the_previous_buffer_on_screen_until_the_new_one_is_ready()
    {
        // Buffer, world anchoring and texture size have to change together, so a rebuild running
        // off the render thread must leave all of them alone until it is finished — otherwise the
        // terrain would be drawn at the wrong scale for the frames in between.
        var source = new TexturePixelSource();
        Action? batch = null;
        TaskCompletionSource? gate = null;
        var model = new MiniMapTerrainTextureModel(
            MiniMapTerrainTextureOptions.ForLargeMap(LargeMapDetail.Standard))
        {
            RefillTimestampProvider = static () => 0L,
            FillScheduler = work =>
            {
                batch = work;
                gate = new TaskCompletionSource();
                return gate.Task;
            },
        };

        void RunPendingBatch()
        {
            var work = batch;
            var completion = gate;
            batch = null;
            gate = null;
            work?.Invoke();
            completion?.SetResult();
        }

        var time = 0.0;
        var zoomedIn = new MapTransform(Vector2.Zero, 1f, new Vector2(850f, 478f));

        // The first update sizes the buffer; EmptyCellCount is meaningless before it.
        model.Update(source, zoomedIn, 0f, time);
        RunPendingBatch();
        for (var frame = 0; frame < 400 && model.EmptyCellCount > 0; frame++)
        {
            time += 1.0;
            model.Update(source, zoomedIn, 0f, time);
            RunPendingBatch();
        }

        Assert.Equal(0, model.EmptyCellCount);
        var presentedTexelBlocks = model.TexelBlocks;
        var presentedSpan = model.SpanBlocks;
        var presentedPixels = model.Pixels;
        var presentedColor = GetPixelAtWorld(model, 0, 0);
        Assert.NotEqual(default, presentedColor);

        // Zoom out far enough that the domain has to grow, and hold the rebuild mid-flight.
        var zoomedOut = new MapTransform(Vector2.Zero, 4f, new Vector2(850f, 478f));
        source.Color = new Rgba32(7, 7, 7, 255);
        time += 1.0;
        Assert.False(model.Update(source, zoomedOut, 0f, time));
        Assert.NotNull(batch);

        for (var frame = 0; frame < 5; frame++)
        {
            time += 1.0;
            Assert.False(model.Update(source, zoomedOut, 0f, time));
            Assert.Equal(presentedTexelBlocks, model.TexelBlocks);
            Assert.Equal(presentedSpan, model.SpanBlocks);
            Assert.Same(presentedPixels, model.Pixels);
            Assert.Equal(presentedColor, GetPixelAtWorld(model, 0, 0));
        }

        RunPendingBatch();
        time += 1.0;

        Assert.True(model.Update(source, zoomedOut, 0f, time), "the rebuilt buffer was never uploaded");
        Assert.True(model.TexelBlocks > presentedTexelBlocks);
        Assert.True(model.SpanBlocks > presentedSpan);
        Assert.NotSame(presentedPixels, model.Pixels);
        Assert.NotEqual(default, GetPixelAtWorld(model, 0, 0));
    }

    [Fact]
    public void The_mini_map_is_paced_by_texels_alone()
    {
        // Its texel ceiling is well under a millisecond of work, so a clock would only ever add
        // jitter. Guarding this keeps the mini map's per-frame cost predictable.
        Assert.Equal(0.0, MiniMapTerrainTextureOptions.MiniMap.RefillTimeBudgetSeconds);
        Assert.Equal(0.0, MiniMapTerrainTextureOptions.MiniMap.ResetRefillTimeBudgetSeconds);
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
/// Pixel source that counts how many per-tile version stamps a poll asks for.
/// </summary>
internal sealed class CountingVersionSource :
    IExploredMapPixelSource,
    IExploredMapTileVersionSource
{
    private readonly TexturePixelSource _pixels = new();
    private long _version = 1;

    public long TileVersionQueries { get; private set; }

    public long MutationVersion => _version;

    public long GetTileMutationVersion(int tileX, int tileZ)
    {
        TileVersionQueries++;
        return _version;
    }

    public void BumpVersion() => _version++;

    public IExploredMapReadSession BeginReadSession() => _pixels.BeginReadSession();
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
