using System.Diagnostics;
using System.Numerics;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;

namespace SurvivalcraftTravelMap.UI;

/// <summary>
/// Optional capability of an <see cref="IExploredMapPixelSource"/>: exposes mutation stamps so a
/// cached rendering can invalidate only the tiles that actually changed. Sources without this
/// capability are treated as immutable after the initial fill (refreshed only on scroll/reset).
/// </summary>
internal interface IExploredMapTileVersionSource
{
    long MutationVersion { get; }

    long GetTileMutationVersion(int tileX, int tileZ);
}

/// <summary>
/// Tunables for <see cref="MiniMapTerrainTextureModel"/>. The mini map and the large map share the
/// cache but not its sizing: the mini map is small, rotates and must stay cheap every frame, while
/// the large map is full screen, never rotates and is allowed a much bigger buffer because it only
/// pays for it while the dialog is open.
/// </summary>
internal sealed record MiniMapTerrainTextureOptions
{
    public static MiniMapTerrainTextureOptions MiniMap { get; } = new();

    /// <summary>
    /// Sizing for the full-screen large map. The buffer is deliberately much larger than the mini
    /// map's: at the default zoom the Standard tier lands on roughly one texel per screen pixel,
    /// which is the point of the exercise.
    /// <para>
    /// The refill is paced by a wall-clock budget, not by the texel cost model. The cost model is
    /// calibrated for cold tiles (a tile's snapshot and its region-sum table have to be faulted in),
    /// which is right the first time the map opens and wildly pessimistic afterwards: a zoom
    /// re-reads tiles that are already resident, where the same "budget" buys about thirty times
    /// the work. Pacing by measured time spends whatever the device can afford per frame, so a zoom
    /// resharpens in a few frames instead of a few seconds, and a slow phone still gets a bounded
    /// frame. The texel budgets stay on as a ceiling for the cold case.
    /// </para>
    /// </summary>
    public static MiniMapTerrainTextureOptions ForLargeMap(LargeMapDetail detail)
    {
        var (maxTextureSize, refillBudget, refillSeconds, uploadInterval) = detail switch
        {
            LargeMapDetail.PowerSaver => (512, 32_768, 0.003, 0.1),
            LargeMapDetail.High => (2_048, 98_304, 0.006, 0.25),
            _ => (1_024, 65_536, 0.005, 0.15),
        };

        return new MiniMapTerrainTextureOptions
        {
            MaxTextureSize = maxTextureSize,
            RefillTexelBudgetPerFrame = refillBudget,
            ResetRefillTexelBudget = refillBudget * 2,
            RefillTimeBudgetSeconds = refillSeconds,
            ResetRefillTimeBudgetSeconds = refillSeconds * 2,

            // ...and better still, off the render thread entirely. Map tile snapshots are
            // immutable and their region-sum tables are published through a thread-safe Lazy, so
            // the fill is a pure reader; the frame then costs only the upload.
            FillsInBackground = true,

            // The large map never rotates, so the domain only has to span the longer viewport
            // edge, and panning is deliberate rather than continuous — both let the margin shrink
            // well below the mini map's, which is worth a whole texture-size step of sharpness.
            CoversRotatingViewport = false,
            DomainMarginFactor = 1.05f,
            UseCoarsePrepass = true,

            // Pinch zoom is continuous, so a single gesture sweeps through several texel sizes.
            // Rebuilding at each one costs a resample and restarts the refill. Short enough that a
            // gesture that has stopped resharpens straight away; long enough that one that has not
            // does its sweeping against a single buffer.
            ResolutionSettleSeconds = 0.05,

            // Uploads replace the whole texture level (the engine has no sub-rectangle upload), so
            // the bigger the buffer the less often it may be pushed. This only bites while a fill
            // is in flight — a settled map uploads nothing at all.
            UploadMinIntervalSeconds = uploadInterval,
            VersionPollIntervalSeconds = 0.5,
        };
    }

    /// <summary>Upper bound for the square buffer's side, in texels. Powers of two only.</summary>
    public int MaxTextureSize { get; init; } = MiniMapTerrainTextureModel.MaxTextureSize;

    public int RefillTexelBudgetPerFrame { get; init; } =
        MiniMapTerrainTextureModel.RefillTexelBudgetPerFrame;

    public int ResetRefillTexelBudget { get; init; } =
        MiniMapTerrainTextureModel.ResetRefillTexelBudget;

    /// <summary>
    /// Wall-clock ceiling for one frame's refill, alongside the texel ceiling — whichever runs out
    /// first ends the frame's work, and a cell already started always finishes. Zero leaves the
    /// texel cost model solely in charge, which is what the mini map wants (its texel ceiling is
    /// well under a millisecond of work, so a clock would never bind) and what keeps the fill
    /// deterministic under test.
    /// </summary>
    public double RefillTimeBudgetSeconds { get; init; }

    public double ResetRefillTimeBudgetSeconds { get; init; }

    public float DomainMarginFactor { get; init; } = MiniMapTerrainTextureModel.DomainMarginFactor;

    public double UploadMinIntervalSeconds { get; init; } =
        MiniMapTerrainTextureModel.UploadMinIntervalSeconds;

    public double VersionPollIntervalSeconds { get; init; } =
        MiniMapTerrainTextureModel.VersionPollIntervalSeconds;

    /// <summary>
    /// How long a newly requested resolution has to stay requested before the buffer is rebuilt for
    /// it — but only while the current buffer still covers the viewport, so this can never delay
    /// the growth a zoom-out needs. Zero rebuilds on the frame the request appears, which is right
    /// for the mini map (its zoom moves in discrete setting steps) and wrong for a pinch.
    /// </summary>
    public double ResolutionSettleSeconds { get; init; }

    /// <summary>
    /// Ceiling on the map tiles a single version poll may stamp. Guards the zoomed-out large map,
    /// where one cell can cover thousands of tiles.
    /// </summary>
    public long MaxVersionScanTilesPerPoll { get; init; } = 65_536;

    /// <summary>
    /// True when the viewport may rotate under the buffer, which forces the domain to cover the
    /// viewport's circumscribed circle. The large map never rotates, so it covers only the longer
    /// viewport edge — worth roughly a factor of 1.4 in span, i.e. a whole texture-size step.
    /// </summary>
    public bool CoversRotatingViewport { get; init; } = true;

    /// <summary>
    /// True to run the refill on a worker thread, leaving the render thread with nothing but the
    /// upload. Worth it for the large map, whose fill is big enough to be felt in the frame; the
    /// mini map's is a fraction of a millisecond and not worth a thread hand-off.
    /// </summary>
    public bool FillsInBackground { get; init; }

    /// <summary>
    /// True to paint every cell once at <see cref="MiniMapTerrainTextureModel.CoarseFactor"/>
    /// granularity (point samples, no region averaging) before refining it to full detail. Costs
    /// about 2% extra work overall but puts a complete — if blocky — map on screen within a few
    /// frames instead of after the seconds a full-detail fill of a large buffer takes.
    /// </summary>
    public bool UseCoarsePrepass { get; init; }
}

/// <summary>
/// CPU model behind the mini map terrain texture cache. Maintains a world-aligned square pixel
/// buffer (1 texel = <see cref="TexelBlocks"/> blocks, power of two) that covers the rotating mini
/// map viewport with scroll margin, refills only cells whose tiles mutated (budgeted per frame,
/// nearest-to-player first), and throttles GPU uploads. The widget draws the whole buffer as a
/// single textured quad per frame instead of thousands of per-cell quads, which is what makes the
/// mini map cheap: staleness only delays *content*, never the quad's world anchoring, because the
/// buffer is world-aligned.
/// </summary>
internal sealed class MiniMapTerrainTextureModel
{
    public const int MaxTextureSize = 512;
    public const int MinTextureSize = 128;
    /// <summary>
    /// Largest refill unit, in texels per side. The unit shrinks as a texel grows (see
    /// <see cref="ChooseCellTexels"/>) so that one unit of work stays bounded.
    /// </summary>
    public const int CellTexels = 64;

    /// <summary>Smallest refill unit, in texels per side. Must be a multiple of CoarseFactor.</summary>
    public const int MinCellTexels = 8;

    // Xaero's minimap caps per-frame map writes at ~100 16x16 tiles (~25k pixels, roughly 8% of a
    // 60fps frame). Use the same scale for steady-state refills; allow a larger burst right after
    // a reset (zoom/setting change) so the map reappears within a few frames.
    public const int RefillTexelBudgetPerFrame = 24_576;
    public const int ResetRefillTexelBudget = 65_536;

    public const float DomainMarginFactor = 1.3f;
    public const double VersionPollIntervalSeconds = 0.15;
    public const double UploadMinIntervalSeconds = 0.15;

    /// <summary>
    /// Side of the texel square a single coarse-pass sample is replicated across. Must divide
    /// <see cref="CellTexels"/>.
    /// </summary>
    public const int CoarseFactor = 8;

    private readonly MiniMapTerrainTextureOptions _options;
    private Rgba32[] _pixels = [];
    private Rgba32[] _scratchPixels = [];
    private long[] _cellVersions = [];
    private long[] _scratchCellVersions = [];
    private CellDetail[] _cellDetails = [];
    private CellDetail[] _scratchCellDetails = [];
    private int[] _cellRefillOrder = [];
    private float _cellOrderAspect = float.NaN;
    private int _textureSize;
    private int _cellTexels = CellTexels;
    private int _texelBlocks;
    private int _pendingTextureSize;
    private int _pendingTexelBlocks;
    private double _pendingResolutionTime;
    private long _originX;
    private long _originZ;
    private float _bakedShadingStrength = float.NaN;
    private long _lastSeenMutationVersion = long.MinValue;
    private double _lastVersionPollTime = double.NegativeInfinity;
    private double _lastUploadTime = double.NegativeInfinity;
    private bool _uploadPending;
    private bool _forceNextUpload;
    private Task? _fillTask;
    private bool _rebuildInFlight;
    private bool _backgroundFillBroken;
    private bool _contentInvalidationRequested;
    private Rgba32[] _presentedPixels = [];
    private int _presentedTextureSize;
    private int _presentedTexelBlocks;
    private long _presentedOriginX;
    private long _presentedOriginZ;

    public MiniMapTerrainTextureModel()
        : this(MiniMapTerrainTextureOptions.MiniMap)
    {
    }

    public MiniMapTerrainTextureModel(MiniMapTerrainTextureOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    private enum CellDetail : byte
    {
        /// <summary>Nothing painted yet; the cell's texels are transparent.</summary>
        Empty,

        /// <summary>Painted, but either at coarse granularity or from stale tile data.</summary>
        Provisional,

        /// <summary>Painted at full detail from current tile data.</summary>
        Complete,
    }

    public MiniMapTerrainTextureOptions Options => _options;

    /// <summary>
    /// Tick source the per-frame refill clock is measured against. Tests replace it with a frozen
    /// one so the fill is paced solely by the deterministic texel cost model.
    /// </summary>
    internal Func<long> RefillTimestampProvider { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>
    /// How a background refill batch is dispatched. Tests substitute an inline scheduler, which
    /// makes the model behave exactly as it does with <c>FillsInBackground</c> off.
    /// </summary>
    internal Func<Action, Task> FillScheduler { get; set; } = static work => Task.Run(work);

    /// <summary>
    /// Wall clock one background batch may run for. Larger than the inline budgets because there
    /// is no frame to protect out there — only the batch's own latency, which decides how quickly
    /// the map resharpens.
    /// </summary>
    private const double BackgroundBatchSeconds = 0.015;

    // What the caller draws. It lags the working state while a rebuild is in flight: the buffer,
    // its world anchoring and the texture size have to change together or the terrain would be
    // drawn at the wrong scale for the frames in between, so the previous buffer stays on screen —
    // correctly anchored, at its old sharpness — until the new one is finished and published.
    public int TextureSize => _presentedTextureSize;

    public int TexelBlocks => _presentedTexelBlocks;

    public long OriginX => _presentedOriginX;

    public long OriginZ => _presentedOriginZ;

    public long SpanBlocks => (long)_presentedTextureSize * _presentedTexelBlocks;

    /// <summary>Span of the buffer being worked on, which is what the domain maths reasons about.</summary>
    private long WorkingSpanBlocks => (long)_textureSize * _texelBlocks;

    public Rgba32[] Pixels => _presentedPixels;

    // Counted with a plain loop rather than LINQ: a settled map still asks every frame whether it
    // has work, and at the High tier that is 4096 cells to look at.
    internal int DirtyCellCount => CountCells(CellDetail.Complete, equal: false);

    /// <summary>Cells that have nothing painted at all — nothing to show, not merely stale.</summary>
    internal int EmptyCellCount => CountCells(CellDetail.Empty, equal: true);

    private int CountCells(CellDetail detail, bool equal)
    {
        var cells = _cellDetails;
        var count = 0;
        for (var index = 0; index < cells.Length; index++)
        {
            if ((cells[index] == detail) == equal)
            {
                count++;
            }
        }

        return count;
    }

    private const long UnfilledVersion = long.MinValue;

    internal int CellTexelsPerSide => _cellTexels;

    private int CellsPerSide => _textureSize / _cellTexels;

    private long CellBlocks => (long)_cellTexels * _texelBlocks;

    /// <summary>
    /// Refill work is charged per texel, but its real cost is dominated by the map tiles a cell
    /// faults in — and a cell covers texelBlocks^2 tiles, so at a zoomed-out texel size a single
    /// 64-texel cell can touch thousands of them and blow a whole frame. Shrink the cell as the
    /// texel grows to keep one unit of work at a couple of tiles, subject to a cell still covering
    /// whole tiles (the version stamp and the aggregate reads both depend on that).
    /// </summary>
    /// <summary>Map tiles one cell covers. Cells always cover whole tiles.</summary>
    internal int TilesPerCell
    {
        get
        {
            var tilesPerSide = Math.Max(1, (int)(CellBlocks / MapTile.Size));
            return tilesPerSide * tilesPerSide;
        }
    }

    private void EnsureScratchPixels(int pixelCount)
    {
        if (_scratchPixels.Length != pixelCount)
        {
            _scratchPixels = new Rgba32[pixelCount];
        }
    }

    private static int ChooseCellTexels(int texelBlocks)
    {
        const int targetCellBlocks = 2 * MapTile.Size;
        var cellTexels = CellTexels;
        while (cellTexels > MinCellTexels && (long)cellTexels * texelBlocks > targetCellBlocks)
        {
            cellTexels >>= 1;
        }

        return cellTexels;
    }

    /// <summary>
    /// Advances the cache one frame. Returns true when the caller must upload
    /// <see cref="Pixels"/> to the GPU texture now.
    /// </summary>
    public bool Update(
        IExploredMapPixelSource source,
        MapTransform transform,
        float heightShadingStrength,
        double time)
    {
        ArgumentNullException.ThrowIfNull(source);

        // A background batch owns the working buffers for as long as it runs, so the frame does
        // nothing at all until it is done — including not reporting an upload, which would read
        // the buffer out from under it. Drawing carries on from the presented state, which the
        // batch never touches.
        if (!TryCollectBackgroundFill())
        {
            return false;
        }

        var viewport = transform.ViewportSize;
        if (!float.IsFinite(viewport.X)
            || !float.IsFinite(viewport.Y)
            || viewport.X <= 0f
            || viewport.Y <= 0f
            || !float.IsFinite(transform.BlocksPerPixel)
            || transform.BlocksPerPixel <= 0f
            || !float.IsFinite(transform.Center.X)
            || !float.IsFinite(transform.Center.Y))
        {
            return false;
        }

        var neededSpan = ViewportExtent(viewport)
            * transform.BlocksPerPixel
            * _options.DomainMarginFactor;
        var (texelBlocks, textureSize) = ChooseResolution(neededSpan);
        var versionSource = source as IExploredMapTileVersionSource;

        // A resolution change is urgent only when the current domain has stopped covering the
        // viewport (zooming out). Refinements (zooming in) can wait for the gesture to settle,
        // which keeps a pinch from rebuilding the buffer at every texel size it sweeps through.
        var rebake = heightShadingStrength != _bakedShadingStrength;
        var wantsResolution = textureSize != _textureSize || texelBlocks != _texelBlocks;
        var settled = wantsResolution && HasResolutionSettled(textureSize, texelBlocks, time);
        if (!wantsResolution)
        {
            _pendingTextureSize = 0;
            _pendingTexelBlocks = 0;
        }

        var reset = _textureSize <= 0
            || rebake
            || (wantsResolution
                && (settled || DomainCapacity(_textureSize, _texelBlocks) < neededSpan));
        if (reset)
        {
            // Rebuilding means allocating and resampling a whole buffer — several milliseconds at
            // the larger tiers, and the one thing left that a zoom could be felt through. Whenever
            // there is a published buffer to keep drawing, hand the entire rebuild to the worker
            // and publish it only once it is ready; the map holds its previous sharpness until
            // then, exactly as the in-place resample used to look, at no cost to the frame.
            if (_presentedTextureSize > 0
                && _options.FillsInBackground
                && !_backgroundFillBroken)
            {
                StartRebuild(
                    source,
                    versionSource,
                    textureSize,
                    texelBlocks,
                    heightShadingStrength,
                    transform.Center,
                    ViewportAspect(viewport),
                    carryOverContent: !rebake,
                    reset ? _options.ResetRefillTexelBudget : _options.RefillTexelBudgetPerFrame);
                return _rebuildInFlight ? false : ConsumeUploadRequest(time);
            }

            // The old buffer is resampled into the new one unless its colours are known to be
            // wrong, so a zoom step never blanks the map: it keeps whatever was on screen and
            // sharpens it in place.
            var carried = Reset(
                textureSize,
                texelBlocks,
                heightShadingStrength,
                transform.Center,
                ViewportAspect(viewport),
                carryOverContent: !rebake);
            Publish(requestUpload: carried);
        }
        else if (RequiresRecenter(transform))
        {
            Scroll(transform.Center);
            Publish(requestUpload: true);
        }
        else
        {
            PollVersions(versionSource, time);
        }

        var texelBudget = reset
            ? _options.ResetRefillTexelBudget
            : _options.RefillTexelBudgetPerFrame;
        if (_options.FillsInBackground && !_backgroundFillBroken)
        {
            // A due upload takes the frame to itself: handing the batch off first would have the
            // worker rewriting texels while the caller is pushing the buffer to the GPU. The batch
            // starts on the next frame instead, which costs nothing — uploads are throttled to one
            // every several frames anyway.
            if (ConsumeUploadRequest(time))
            {
                return true;
            }

            if (DirtyCellCount > 0)
            {
                StartBackgroundFill(source, versionSource, texelBudget);
                if (_fillTask is not null)
                {
                    return false;
                }

                // An inline batch (tests) has already finished and may have queued an upload.
                return ConsumeUploadRequest(time);
            }

            return false;
        }

        RefillDirtyCells(
            source,
            versionSource,
            texelBudget,
            reset ? _options.ResetRefillTimeBudgetSeconds : _options.RefillTimeBudgetSeconds);
        return ConsumeUploadRequest(time);
    }

    /// <summary>
    /// Returns false while a background batch still owns the buffers. Also folds in a finished
    /// batch: a batch that faulted retires background filling for good and falls back to filling
    /// inline, because a half-painted map beats one that has silently stopped updating.
    /// </summary>
    private bool TryCollectBackgroundFill()
    {
        if (_fillTask is not { } running)
        {
            ApplyRequestedContentInvalidation();
            return true;
        }

        if (!running.IsCompleted)
        {
            return false;
        }

        _fillTask = null;
        if (running.Exception is { } fault)
        {
            _backgroundFillBroken = true;
            ReportBackgroundFillFailure(fault);
        }

        if (_rebuildInFlight)
        {
            _rebuildInFlight = false;
            Publish(requestUpload: true);
        }

        ApplyRequestedContentInvalidation();
        return true;
    }

    /// <summary>
    /// Makes the working buffer the one the caller draws. Called on the render thread only, and
    /// only when no batch is in flight, so the swap is never observed half-done.
    /// </summary>
    /// <param name="requestUpload">
    /// False when the buffer being published has nothing on it yet: claiming an upload would spend
    /// the frame pushing a blank texture and cost the first fill batch a frame for nothing.
    /// </param>
    private void Publish(bool requestUpload)
    {
        _presentedPixels = _pixels;
        _presentedTextureSize = _textureSize;
        _presentedTexelBlocks = _texelBlocks;
        _presentedOriginX = _originX;
        _presentedOriginZ = _originZ;
        if (requestUpload)
        {
            _uploadPending = true;
            _forceNextUpload = true;
        }
    }

    /// <summary>
    /// Runs a whole resolution change — allocate, resample from the published buffer, and the
    /// first refill batch — off the render thread. The published state is left alone until it
    /// completes, so the map keeps drawing at its previous resolution in the meantime.
    /// </summary>
    private void StartRebuild(
        IExploredMapPixelSource source,
        IExploredMapTileVersionSource? versionSource,
        int textureSize,
        int texelBlocks,
        float shadingStrength,
        Vector2 center,
        float viewportAspect,
        bool carryOverContent,
        int texelBudget)
    {
        var task = FillScheduler(() =>
        {
            Reset(textureSize, texelBlocks, shadingStrength, center, viewportAspect, carryOverContent);
            RefillDirtyCells(source, versionSource, texelBudget, BackgroundBatchSeconds);
        });

        if (task.IsCompleted)
        {
            _fillTask = null;
            _rebuildInFlight = false;
            if (task.Exception is { } inlineFault)
            {
                _backgroundFillBroken = true;
                ReportBackgroundFillFailure(inlineFault);
            }

            Publish(requestUpload: true);
            return;
        }

        _fillTask = task;
        _rebuildInFlight = true;
    }

    private void StartBackgroundFill(
        IExploredMapPixelSource source,
        IExploredMapTileVersionSource? versionSource,
        int texelBudget)
    {
        var task = FillScheduler(() => RefillDirtyCells(
            source,
            versionSource,
            texelBudget,
            BackgroundBatchSeconds));

        // An inline scheduler (tests) finishes before returning, and nothing owns the buffers then.
        _fillTask = task.IsCompleted ? null : task;
        if (task.Exception is { } fault)
        {
            _backgroundFillBroken = true;
            ReportBackgroundFillFailure(fault);
        }
    }

    private static void ReportBackgroundFillFailure(Exception fault) =>
        BackgroundFillFailureReporter?.Invoke(fault);

    /// <summary>
    /// Where a faulted background batch is reported. Set by the widget so the model itself stays
    /// free of engine references (the test assembly has no engine to log to).
    /// </summary>
    internal static Action<Exception>? BackgroundFillFailureReporter { get; set; }

    private void ApplyRequestedContentInvalidation()
    {
        if (!_contentInvalidationRequested)
        {
            return;
        }

        _contentInvalidationRequested = false;
        ClearContent();
    }

    /// <summary>
    /// Drops every painted texel and repaints from scratch. Version stamps cannot tell a changed
    /// tile apart from a switch to a different world layer (surface/cave), and the latter must not
    /// leave the previous layer's terrain on screen for the seconds a budgeted refill of a large
    /// buffer takes — so the caller announces those switches explicitly.
    /// </summary>
    public void InvalidateContent()
    {
        if (_fillTask is not null)
        {
            // A batch owns the buffers; the next Update clears them once it has finished.
            _contentInvalidationRequested = true;
            return;
        }

        ClearContent();
    }

    private void ClearContent()
    {
        if (_textureSize <= 0)
        {
            return;
        }

        Array.Clear(_pixels);
        Array.Fill(_cellVersions, UnfilledVersion);
        Array.Fill(_cellDetails, CellDetail.Empty);
        _lastSeenMutationVersion = long.MinValue;

        // The cleared buffer has to reach the GPU: dropping the previous world layer is the point.
        Publish(requestUpload: true);
    }

    /// <summary>
    /// Forces the next <see cref="Update"/> to request an upload even if no cell changed — used
    /// after the GPU texture object is (re)created, e.g. on device reset, when its content is gone
    /// while the CPU buffer is still valid.
    /// </summary>
    public void InvalidateUpload()
    {
        if (_textureSize > 0)
        {
            _uploadPending = true;
            _forceNextUpload = true;
        }
    }

    /// <summary>
    /// Blocks the domain must span across the viewport. A rotating viewport sweeps its
    /// circumscribed circle, an unrotated one only needs its longer edge.
    /// </summary>
    private float ViewportExtent(Vector2 viewport) => _options.CoversRotatingViewport
        ? viewport.Length()
        : MathF.Max(viewport.X, viewport.Y);

    /// <summary>
    /// Blocks a buffer of this shape can be counted on to cover. Origins are aligned to whole
    /// cells, which costs up to half a cell per side, and the recenter guard consumes another half;
    /// only the remainder may be counted on. The margin follows the cell size, which shrinks as
    /// texels grow — so a coarse texel does not also force a domain covering several times the
    /// viewport (and with it several times the map tiles to read).
    /// </summary>
    private static float DomainCapacity(int textureSize, int texelBlocks) =>
        (float)(textureSize - (2 * ChooseCellTexels(texelBlocks))) * texelBlocks;

    /// <summary>
    /// Tracks how long the requested resolution has been the requested one, so a rebuild can wait
    /// out a gesture instead of firing on every texel size the gesture passes through.
    /// </summary>
    private bool HasResolutionSettled(int textureSize, int texelBlocks, double time)
    {
        if (textureSize != _pendingTextureSize || texelBlocks != _pendingTexelBlocks)
        {
            _pendingTextureSize = textureSize;
            _pendingTexelBlocks = texelBlocks;
            _pendingResolutionTime = time;
        }

        return time - _pendingResolutionTime >= _options.ResolutionSettleSeconds;
    }

    private (int TexelBlocks, int TextureSize) ChooseResolution(float neededSpanBlocks)
    {
        static float Capacity(int textureSize, int texelBlocks) =>
            DomainCapacity(textureSize, texelBlocks);

        var maxTextureSize = Math.Max(MinTextureSize, _options.MaxTextureSize);
        var span = Math.Clamp(neededSpanBlocks, 1f, 1 << 24);
        var texelBlocks = 1;

        // A region read may not straddle a map tile, so a texel can aggregate at most one whole
        // tile; past that the domain has to grow instead of coarsening further.
        while (Capacity(maxTextureSize, texelBlocks) < span && texelBlocks < MapTile.Size)
        {
            texelBlocks <<= 1;
        }

        var textureSize = MinTextureSize;
        while (textureSize < maxTextureSize && Capacity(textureSize, texelBlocks) < span)
        {
            textureSize <<= 1;
        }

        return (texelBlocks, textureSize);
    }

    /// <summary>Returns true when the previous buffer's content was carried into the new one.</summary>
    private bool Reset(
        int textureSize,
        int texelBlocks,
        float shadingStrength,
        Vector2 center,
        float viewportAspect,
        bool carryOverContent)
    {
        var previousPixels = _pixels;
        var previousSize = _textureSize;
        var previousTexelBlocks = _texelBlocks;
        var previousOriginX = _originX;
        var previousOriginZ = _originZ;
        var carryOver = carryOverContent
            && previousSize > 0
            && previousTexelBlocks > 0
            && previousPixels.Length == previousSize * previousSize;

        _textureSize = textureSize;
        _texelBlocks = texelBlocks;
        _cellTexels = ChooseCellTexels(texelBlocks);
        _bakedShadingStrength = shadingStrength;
        _pendingTextureSize = 0;
        _pendingTexelBlocks = 0;
        var pixelCount = textureSize * textureSize;
        if (_pixels.Length != pixelCount)
        {
            // The scratch buffer is only wanted for scrolling and resampling, and zeroing a second
            // full-size buffer is a real cost at the larger tiers — leave it until it is needed.
            _pixels = new Rgba32[pixelCount];
            _scratchPixels = [];
        }
        else if (carryOver)
        {
            // The resample reads the previous buffer, so it may not also write into it.
            EnsureScratchPixels(pixelCount);
            (_pixels, _scratchPixels) = (_scratchPixels, _pixels);
        }

        var cellCount = CellsPerSide * CellsPerSide;
        if (_cellVersions.Length != cellCount)
        {
            _cellVersions = new long[cellCount];
            _scratchCellVersions = new long[cellCount];
            _cellDetails = new CellDetail[cellCount];
            _scratchCellDetails = new CellDetail[cellCount];
        }

        if (_cellRefillOrder.Length != cellCount || viewportAspect != _cellOrderAspect)
        {
            _cellOrderAspect = viewportAspect;
            _cellRefillOrder = CreateCenterOutOrder(CellsPerSide, viewportAspect);
        }

        Array.Fill(_cellVersions, UnfilledVersion);
        Array.Fill(_cellDetails, CellDetail.Empty);
        _lastSeenMutationVersion = long.MinValue;
        (_originX, _originZ) = AlignOrigin(center);
        if (carryOver)
        {
            Resample(
                previousPixels,
                previousSize,
                previousTexelBlocks,
                previousOriginX,
                previousOriginZ);
        }
        else
        {
            Array.Clear(_pixels);
        }

        // Whether this becomes visible — and so whether it needs uploading — is Publish's call.
        return carryOver;
    }

    /// <summary>
    /// Repaints the new buffer from the previous one so a resolution change keeps whatever terrain
    /// was already on screen — magnified when zooming in, minified when zooming out — instead of
    /// dropping back to a blank buffer and a coarse preview. Nearest-neighbour is enough: the
    /// budgeted refill replaces every texel with true data within seconds, and what this exists to
    /// prevent is the map visibly dissolving each time the zoom crosses a texel-size step.
    /// </summary>
    private void Resample(
        Rgba32[] source,
        int sourceSize,
        int sourceTexelBlocks,
        long sourceOriginX,
        long sourceOriginZ)
    {
        var destination = _pixels;
        Array.Clear(destination);

        // Both texel sizes are powers of two, so the world-to-source-texel division is a shift.
        var texelShift = BitOperations.TrailingZeroCount(_texelBlocks);
        var sourceShift = BitOperations.TrailingZeroCount(sourceTexelBlocks);
        var deltaX = _originX - sourceOriginX;
        var deltaZ = _originZ - sourceOriginZ;
        var size = _textureSize;

        // Only the texels that land inside the previous domain can be carried over, and a zoom-out
        // leaves most of the new buffer outside it. Walking just the overlap keeps the rebuild
        // frame proportional to what is actually copied rather than to the whole buffer.
        var sourceSpan = (long)sourceSize << sourceShift;
        var startX = ClampedTexelBound(-deltaX, _texelBlocks, size);
        var endX = ClampedTexelBound(sourceSpan - deltaX, _texelBlocks, size);
        var startZ = ClampedTexelBound(-deltaZ, _texelBlocks, size);
        var endZ = ClampedTexelBound(sourceSpan - deltaZ, _texelBlocks, size);
        for (var z = startZ; z < endZ; z++)
        {
            var sourceZ = (deltaZ + ((long)z << texelShift)) >> sourceShift;
            if (sourceZ < 0 || sourceZ >= sourceSize)
            {
                continue;
            }

            var sourceRow = (int)sourceZ * sourceSize;
            var destinationRow = z * size;
            for (var x = startX; x < endX; x++)
            {
                var sourceX = (deltaX + ((long)x << texelShift)) >> sourceShift;
                if (sourceX >= 0 && sourceX < sourceSize)
                {
                    destination[destinationRow + x] = source[sourceRow + (int)sourceX];
                }
            }
        }

        MarkResampledCells(sourceOriginX, sourceOriginZ, (long)sourceSize * sourceTexelBlocks);
    }

    /// <summary>
    /// First destination texel at or past <paramref name="blocksFromOrigin"/>, clamped to the
    /// buffer.
    /// </summary>
    private static int ClampedTexelBound(long blocksFromOrigin, int texelBlocks, int textureSize) =>
        (int)Math.Clamp(
            (long)Math.Ceiling(blocksFromOrigin / (double)texelBlocks),
            0,
            textureSize);

    /// <summary>
    /// Marks every cell the resample could have painted as provisional rather than empty: it has
    /// something to show, so the coarse prepass must skip it and go straight to refinement.
    /// </summary>
    private void MarkResampledCells(long sourceOriginX, long sourceOriginZ, long sourceSpanBlocks)
    {
        var cells = CellsPerSide;
        var cellBlocks = CellBlocks;
        for (var cellZ = 0; cellZ < cells; cellZ++)
        {
            var startZ = _originZ + (cellZ * cellBlocks);
            if (startZ + cellBlocks <= sourceOriginZ || startZ >= sourceOriginZ + sourceSpanBlocks)
            {
                continue;
            }

            for (var cellX = 0; cellX < cells; cellX++)
            {
                var startX = _originX + (cellX * cellBlocks);
                if (startX + cellBlocks <= sourceOriginX
                    || startX >= sourceOriginX + sourceSpanBlocks)
                {
                    continue;
                }

                _cellDetails[(cellZ * cells) + cellX] = CellDetail.Provisional;
            }
        }
    }

    private (long X, long Z) AlignOrigin(Vector2 center)
    {
        var cellBlocks = CellBlocks;
        var half = WorkingSpanBlocks / 2;
        var x = (long)MathF.Floor(center.X) - half;
        var z = (long)MathF.Floor(center.Y) - half;
        return (RoundAlign(x, cellBlocks), RoundAlign(z, cellBlocks));
    }

    private static long RoundAlign(long value, long alignment) =>
        (long)Math.Floor((value + (alignment / 2.0)) / alignment) * alignment;

    private bool RequiresRecenter(MapTransform transform)
    {
        // A rotating viewport needs coverage for its circumscribed circle around the map center.
        // Recenter once that circle gets within half a cell of the domain's edge.
        var neededHalf = ViewportExtent(transform.ViewportSize) * transform.BlocksPerPixel / 2f;
        var guard = CellBlocks / 2.0;
        var half = WorkingSpanBlocks / 2.0;
        var domainCenterX = _originX + half;
        var domainCenterZ = _originZ + half;
        return Math.Abs(transform.Center.X - domainCenterX) + neededHalf > half - guard
            || Math.Abs(transform.Center.Y - domainCenterZ) + neededHalf > half - guard;
    }

    private void Scroll(Vector2 center)
    {
        var (newOriginX, newOriginZ) = AlignOrigin(center);
        var deltaTexelsX = (newOriginX - _originX) / _texelBlocks;
        var deltaTexelsZ = (newOriginZ - _originZ) / _texelBlocks;
        if (deltaTexelsX == 0 && deltaTexelsZ == 0)
        {
            return;
        }

        var size = _textureSize;
        EnsureScratchPixels(size * size);
        Array.Clear(_scratchPixels);
        if (Math.Abs(deltaTexelsX) < size && Math.Abs(deltaTexelsZ) < size)
        {
            var copyWidth = size - (int)Math.Abs(deltaTexelsX);
            var sourceX = (int)Math.Max(0, deltaTexelsX);
            var destinationX = (int)Math.Max(0, -deltaTexelsX);
            for (var z = 0; z < size; z++)
            {
                var sourceZ = z + deltaTexelsZ;
                if (sourceZ < 0 || sourceZ >= size)
                {
                    continue;
                }

                Array.Copy(
                    _pixels,
                    ((int)sourceZ * size) + sourceX,
                    _scratchPixels,
                    (z * size) + destinationX,
                    copyWidth);
            }
        }

        (_pixels, _scratchPixels) = (_scratchPixels, _pixels);

        var cells = CellsPerSide;
        var deltaCellsX = deltaTexelsX / _cellTexels;
        var deltaCellsZ = deltaTexelsZ / _cellTexels;
        Array.Fill(_scratchCellVersions, UnfilledVersion);
        Array.Fill(_scratchCellDetails, CellDetail.Empty);
        for (var cellZ = 0; cellZ < cells; cellZ++)
        {
            var sourceZ = cellZ + deltaCellsZ;
            if (sourceZ < 0 || sourceZ >= cells)
            {
                continue;
            }

            for (var cellX = 0; cellX < cells; cellX++)
            {
                var sourceX = cellX + deltaCellsX;
                if (sourceX >= 0 && sourceX < cells)
                {
                    var target = (cellZ * cells) + cellX;
                    var origin = ((int)sourceZ * cells) + (int)sourceX;
                    _scratchCellVersions[target] = _cellVersions[origin];
                    _scratchCellDetails[target] = _cellDetails[origin];
                }
            }
        }

        (_cellVersions, _scratchCellVersions) = (_scratchCellVersions, _cellVersions);
        (_cellDetails, _scratchCellDetails) = (_scratchCellDetails, _cellDetails);
        _originX = newOriginX;
        _originZ = newOriginZ;
        _uploadPending = true;
        _forceNextUpload = true;
    }

    private void PollVersions(IExploredMapTileVersionSource? versionSource, double time)
    {
        if (versionSource is null
            || time - _lastVersionPollTime < _options.VersionPollIntervalSeconds)
        {
            return;
        }

        _lastVersionPollTime = time;

        // Stamping a cell costs one lookup per map tile it covers, so a zoomed-out domain can
        // sweep millions of tiles per poll. Past the budget, stop polling entirely: at that zoom a
        // texel already averages a whole tile, so live edits are invisible anyway, and the next
        // scroll or zoom repaints from scratch.
        var cells = CellsPerSide;
        var scanTiles = (long)cells * cells * TilesPerCell;
        if (scanTiles > _options.MaxVersionScanTilesPerPoll)
        {
            return;
        }

        var mutationVersion = versionSource.MutationVersion;
        if (mutationVersion == _lastSeenMutationVersion)
        {
            return;
        }

        _lastSeenMutationVersion = mutationVersion;
        for (var cellZ = 0; cellZ < cells; cellZ++)
        {
            for (var cellX = 0; cellX < cells; cellX++)
            {
                var index = (cellZ * cells) + cellX;
                if (_cellDetails[index] == CellDetail.Empty)
                {
                    continue;
                }

                if (_cellVersions[index] != GetCellVersion(versionSource, cellX, cellZ))
                {
                    // Keep the stale pixels on screen and queue a full repaint: blanking here
                    // would make explored terrain flicker out while the player walks around.
                    _cellDetails[index] = CellDetail.Provisional;
                }
            }
        }
    }

    private long GetCellVersion(IExploredMapTileVersionSource versionSource, int cellX, int cellZ)
    {
        // Cell origins are aligned to whole 64-block map tiles, so a cell covers exactly
        // TexelBlocks x TexelBlocks tiles; its stamp is the newest stamp of any covered tile.
        var tileStartX = (int)((_originX + (cellX * CellBlocks)) >> 6);
        var tileStartZ = (int)((_originZ + (cellZ * CellBlocks)) >> 6);
        var tilesPerSide = _texelBlocks;
        var version = long.MinValue + 1;
        for (var z = 0; z < tilesPerSide; z++)
        {
            for (var x = 0; x < tilesPerSide; x++)
            {
                version = Math.Max(
                    version,
                    versionSource.GetTileMutationVersion(tileStartX + x, tileStartZ + z));
            }
        }

        return version;
    }

    private void RefillDirtyCells(
        IExploredMapPixelSource source,
        IExploredMapTileVersionSource? versionSource,
        int texelBudget,
        double timeBudgetSeconds)
    {
        var costPerCell = CellRefillCost(coarse: false);
        var coarseCostPerCell = CellRefillCost(coarse: true);
        var deadline = timeBudgetSeconds > 0.0
            ? RefillTimestampProvider() + (long)(timeBudgetSeconds * Stopwatch.Frequency)
            : (long?)null;
        IExploredMapReadSession? session = null;
        try
        {
            if (_options.UseCoarsePrepass)
            {
                // Preview every unpainted cell first: a complete blocky map beats a sharp map that
                // is still mostly empty, and the refinement below then works on a visible map.
                foreach (var index in _cellRefillOrder)
                {
                    if (texelBudget < coarseCostPerCell || IsOutOfTime(deadline))
                    {
                        break;
                    }

                    if (_cellDetails[index] != CellDetail.Empty)
                    {
                        continue;
                    }

                    session ??= source.BeginReadSession();
                    FillCell(session, versionSource, index, coarse: true);
                    texelBudget -= coarseCostPerCell;
                }
            }

            foreach (var index in _cellRefillOrder)
            {
                if (texelBudget < costPerCell || IsOutOfTime(deadline))
                {
                    break;
                }

                if (_cellDetails[index] == CellDetail.Complete)
                {
                    continue;
                }

                session ??= source.BeginReadSession();
                FillCell(session, versionSource, index, coarse: false);
                texelBudget -= costPerCell;
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private bool IsOutOfTime(long? deadline) =>
        deadline.HasValue && RefillTimestampProvider() - deadline.Value >= 0;

    /// <summary>
    /// What refilling one cell costs, in budget units. Texels written are only part of it: the
    /// dominant term is faulting in the map tiles the cell covers (a snapshot, and for a full pass
    /// its region-sum table), and a cell covers more tiles the further out the map is zoomed.
    /// Measured on a desktop CPU, one tile costs about as much as 400 texels for a full pass and
    /// about half that for a coarse one, which point-samples and so never builds region sums.
    /// </summary>
    private int CellRefillCost(bool coarse)
    {
        const int fullTileCostInTexels = 400;
        var texelsPerCell = _cellTexels * _cellTexels;
        var tilesPerCell = TilesPerCell;
        return coarse
            ? (texelsPerCell / CoarseFactor) + (tilesPerCell * (fullTileCostInTexels / 2))
            : texelsPerCell + (tilesPerCell * fullTileCostInTexels);
    }

    private void FillCell(
        IExploredMapReadSession session,
        IExploredMapTileVersionSource? versionSource,
        int index,
        bool coarse)
    {
        var cells = CellsPerSide;
        var cellX = index % cells;
        var cellZ = index / cells;
        var stamp = versionSource is null ? 0L : GetCellVersion(versionSource, cellX, cellZ);
        if (coarse)
        {
            FillCellCoarse(session, cellX, cellZ);
        }
        else
        {
            FillCell(session, cellX, cellZ);
        }

        _cellVersions[index] = stamp;
        _cellDetails[index] = coarse ? CellDetail.Provisional : CellDetail.Complete;
        _uploadPending = true;
    }

    private void FillCell(IExploredMapReadSession session, int cellX, int cellZ)
    {
        var size = _textureSize;
        var texelBlocks = _texelBlocks;
        var aggregate = texelBlocks > 1 ? session as IExploredMapAggregateReadSession : null;
        var strength = _bakedShadingStrength;
        var startTexelX = cellX * _cellTexels;
        var startTexelZ = cellZ * _cellTexels;
        var cellWorldX = _originX + ((long)startTexelX * texelBlocks);
        var cellWorldZ = _originZ + ((long)startTexelZ * texelBlocks);
        var worldInRange = IsWorldInRange(cellWorldX, cellWorldZ);
        for (var z = 0; z < _cellTexels; z++)
        {
            var rowIndex = ((startTexelZ + z) * size) + startTexelX;
            if (!worldInRange)
            {
                Array.Clear(_pixels, rowIndex, _cellTexels);
                continue;
            }

            var worldZ = (int)(cellWorldZ + ((long)z * texelBlocks));
            for (var x = 0; x < _cellTexels; x++)
            {
                var worldX = (int)(cellWorldX + ((long)x * texelBlocks));
                var found = aggregate is not null
                    ? aggregate.TryGetExploredTerrainRegion(
                        worldX,
                        worldZ,
                        texelBlocks,
                        texelBlocks,
                        out var pixel)
                    : session.TryGetExploredTerrainPixel(worldX, worldZ, out pixel);

                // Bake relief shading at full brightness; the day/night tint is applied per frame
                // by the widget as a vertex color, so light changes never invalidate this cache.
                _pixels[rowIndex + x] = found
                    ? TerrainHeightShading.Apply(pixel.Color, pixel.HeightShade, strength, 1f)
                    : default;
            }
        }
    }

    /// <summary>
    /// Paints a cell from one point sample per <see cref="CoarseFactor"/> square of texels. Point
    /// sampling is what makes this cheap: it never touches a tile's region-sum table, which is the
    /// dominant cost of a full fill and is built per tile on first aggregate read.
    /// </summary>
    private void FillCellCoarse(IExploredMapReadSession session, int cellX, int cellZ)
    {
        var size = _textureSize;
        var texelBlocks = _texelBlocks;
        var strength = _bakedShadingStrength;
        var startTexelX = cellX * _cellTexels;
        var startTexelZ = cellZ * _cellTexels;
        var cellWorldX = _originX + ((long)startTexelX * texelBlocks);
        var cellWorldZ = _originZ + ((long)startTexelZ * texelBlocks);
        if (!IsWorldInRange(cellWorldX, cellWorldZ))
        {
            for (var z = 0; z < _cellTexels; z++)
            {
                Array.Clear(_pixels, ((startTexelZ + z) * size) + startTexelX, _cellTexels);
            }

            return;
        }

        for (var blockZ = 0; blockZ < _cellTexels; blockZ += CoarseFactor)
        {
            var worldZ = (int)(cellWorldZ + ((long)blockZ * texelBlocks));
            for (var blockX = 0; blockX < _cellTexels; blockX += CoarseFactor)
            {
                var worldX = (int)(cellWorldX + ((long)blockX * texelBlocks));
                var color = session.TryGetExploredTerrainPixel(worldX, worldZ, out var pixel)
                    ? TerrainHeightShading.Apply(pixel.Color, pixel.HeightShade, strength, 1f)
                    : default;
                for (var z = 0; z < CoarseFactor; z++)
                {
                    var rowIndex = ((startTexelZ + blockZ + z) * size) + startTexelX + blockX;
                    _pixels.AsSpan(rowIndex, CoarseFactor).Fill(color);
                }
            }
        }
    }

    private bool IsWorldInRange(long cellWorldX, long cellWorldZ) =>
        cellWorldX >= int.MinValue
        && cellWorldZ >= int.MinValue
        && cellWorldX + CellBlocks <= int.MaxValue
        && cellWorldZ + CellBlocks <= int.MaxValue;

    private bool ConsumeUploadRequest(double time)
    {
        if (!_uploadPending)
        {
            return false;
        }

        if (!_forceNextUpload && time - _lastUploadTime < _options.UploadMinIntervalSeconds)
        {
            return false;
        }

        _uploadPending = false;
        _forceNextUpload = false;
        _lastUploadTime = time;
        return true;
    }

    /// <summary>
    /// Blocks the domain spans across the viewport's short edge relative to its long one, or 1 for
    /// a viewport that rotates under the buffer. Refill order follows this so a wide screen fills
    /// what it can actually show before it fills the domain's off-screen top and bottom.
    /// </summary>
    private float ViewportAspect(Vector2 viewport) => _options.CoversRotatingViewport
        ? 1f
        : Math.Clamp(viewport.Y / viewport.X, 0.25f, 4f);

    private static int[] CreateCenterOutOrder(int cellsPerSide, float viewportAspect)
    {
        var order = Enumerable.Range(0, cellsPerSide * cellsPerSide).ToArray();
        var center = (cellsPerSide - 1) / 2f;
        var zWeight = 1f / (viewportAspect * viewportAspect);
        return order
            .OrderBy(index =>
            {
                var x = (index % cellsPerSide) - center;
                var z = (index / cellsPerSide) - center;
                return (x * x) + (z * z * zWeight);
            })
            .ToArray();
    }
}

/// <summary>
/// Builds the screen-space triangles (with texture coordinates) that present the cached terrain
/// texture: the world-aligned domain quad transformed to screen, clipped to the map shape, and
/// fan-triangulated. UVs are recovered per clipped vertex through the inverse map transform, so
/// rotation and partial clipping need no special casing.
/// </summary>
internal static class MiniMapTerrainTextureQuad
{
    public readonly record struct Vertex(Vector2 Position, Vector2 TexCoord);

    public static IReadOnlyList<Vertex> BuildTriangles(
        MapTransform transform,
        MapShapeGeometry geometry,
        long originX,
        long originZ,
        long spanBlocks)
    {
        if (spanBlocks <= 0)
        {
            return [];
        }

        var corners = new[]
        {
            transform.WorldToScreen(new Vector2(originX, originZ)),
            transform.WorldToScreen(new Vector2(originX + spanBlocks, originZ)),
            transform.WorldToScreen(new Vector2(originX + spanBlocks, originZ + spanBlocks)),
            transform.WorldToScreen(new Vector2(originX, originZ + spanBlocks)),
        };
        var clipped = geometry.ClipPolygon(corners);
        if (clipped.Count < 3)
        {
            return [];
        }

        var vertices = new List<Vertex>((clipped.Count - 2) * 3);
        var first = ToVertex(clipped[0], transform, originX, originZ, spanBlocks);
        for (var index = 1; index < clipped.Count - 1; index++)
        {
            vertices.Add(first);
            vertices.Add(ToVertex(clipped[index], transform, originX, originZ, spanBlocks));
            vertices.Add(ToVertex(clipped[index + 1], transform, originX, originZ, spanBlocks));
        }

        return vertices;
    }

    private static Vertex ToVertex(
        Vector2 screen,
        MapTransform transform,
        long originX,
        long originZ,
        long spanBlocks)
    {
        var world = transform.ScreenToWorld(screen);
        return new Vertex(
            screen,
            new Vector2(
                (world.X - originX) / spanBlocks,
                (world.Y - originZ) / spanBlocks));
    }
}
