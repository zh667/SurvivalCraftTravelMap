using System.Numerics;
using SurvivalcraftTravelMap.Map;

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
    public const int CellTexels = 64;

    // Xaero's minimap caps per-frame map writes at ~100 16x16 tiles (~25k pixels, roughly 8% of a
    // 60fps frame). Use the same scale for steady-state refills; allow a larger burst right after
    // a reset (zoom/setting change) so the map reappears within a few frames.
    public const int RefillTexelBudgetPerFrame = 24_576;
    public const int ResetRefillTexelBudget = 65_536;

    public const float DomainMarginFactor = 1.3f;
    public const double VersionPollIntervalSeconds = 0.15;
    public const double UploadMinIntervalSeconds = 0.15;

    private Rgba32[] _pixels = [];
    private Rgba32[] _scratchPixels = [];
    private long[] _cellVersions = [];
    private long[] _scratchCellVersions = [];
    private int[] _cellRefillOrder = [];
    private int _textureSize;
    private int _texelBlocks;
    private long _originX;
    private long _originZ;
    private float _bakedShadingStrength = float.NaN;
    private long _lastSeenMutationVersion = long.MinValue;
    private double _lastVersionPollTime = double.NegativeInfinity;
    private double _lastUploadTime = double.NegativeInfinity;
    private bool _uploadPending;
    private bool _forceNextUpload;

    public int TextureSize => _textureSize;

    public int TexelBlocks => _texelBlocks;

    public long OriginX => _originX;

    public long OriginZ => _originZ;

    public long SpanBlocks => (long)_textureSize * _texelBlocks;

    public Rgba32[] Pixels => _pixels;

    internal int DirtyCellCount => _cellVersions.Count(version => version == UnfilledVersion);

    private const long UnfilledVersion = long.MinValue;

    private int CellsPerSide => _textureSize / CellTexels;

    private long CellBlocks => (long)CellTexels * _texelBlocks;

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

        var neededSpan = viewport.Length() * transform.BlocksPerPixel * DomainMarginFactor;
        var (texelBlocks, textureSize) = ChooseResolution(neededSpan);
        var versionSource = source as IExploredMapTileVersionSource;

        var reset = textureSize != _textureSize
            || texelBlocks != _texelBlocks
            || heightShadingStrength != _bakedShadingStrength;
        if (reset)
        {
            Reset(textureSize, texelBlocks, heightShadingStrength, transform.Center);
        }
        else if (RequiresRecenter(transform))
        {
            Scroll(transform.Center);
        }
        else
        {
            PollVersions(versionSource, time);
        }

        var budget = reset ? ResetRefillTexelBudget : RefillTexelBudgetPerFrame;
        RefillDirtyCells(source, versionSource, budget);
        return ConsumeUploadRequest(time);
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

    private static (int TexelBlocks, int TextureSize) ChooseResolution(float neededSpanBlocks)
    {
        // Origins are aligned to whole cells, which costs up to half a cell per side, and the
        // recenter guard consumes another half; only the remaining capacity may be counted on to
        // cover the viewport.
        static float Capacity(int textureSize, int texelBlocks) =>
            (float)(textureSize - (2 * CellTexels)) * texelBlocks;

        var span = Math.Clamp(neededSpanBlocks, 1f, 1 << 24);
        var texelBlocks = 1;
        while (Capacity(MaxTextureSize, texelBlocks) < span)
        {
            texelBlocks <<= 1;
        }

        var textureSize = MinTextureSize;
        while (textureSize < MaxTextureSize && Capacity(textureSize, texelBlocks) < span)
        {
            textureSize <<= 1;
        }

        return (texelBlocks, textureSize);
    }

    private void Reset(int textureSize, int texelBlocks, float shadingStrength, Vector2 center)
    {
        _textureSize = textureSize;
        _texelBlocks = texelBlocks;
        _bakedShadingStrength = shadingStrength;
        var pixelCount = textureSize * textureSize;
        if (_pixels.Length != pixelCount)
        {
            _pixels = new Rgba32[pixelCount];
            _scratchPixels = new Rgba32[pixelCount];
        }
        else
        {
            Array.Clear(_pixels);
        }

        var cellCount = CellsPerSide * CellsPerSide;
        if (_cellVersions.Length != cellCount)
        {
            _cellVersions = new long[cellCount];
            _scratchCellVersions = new long[cellCount];
            _cellRefillOrder = CreateCenterOutOrder(CellsPerSide);
        }

        Array.Fill(_cellVersions, UnfilledVersion);
        (_originX, _originZ) = AlignOrigin(center);
        _forceNextUpload = true;
    }

    private (long X, long Z) AlignOrigin(Vector2 center)
    {
        var cellBlocks = CellBlocks;
        var half = SpanBlocks / 2;
        var x = (long)MathF.Floor(center.X) - half;
        var z = (long)MathF.Floor(center.Y) - half;
        return (RoundAlign(x, cellBlocks), RoundAlign(z, cellBlocks));
    }

    private static long RoundAlign(long value, long alignment) =>
        (long)Math.Floor((value + (alignment / 2.0)) / alignment) * alignment;

    private bool RequiresRecenter(MapTransform transform)
    {
        // The viewport can rotate, so it needs coverage for its circumscribed circle around the
        // map center. Recenter once that circle gets within half a cell of the domain's edge.
        var neededHalf = transform.ViewportSize.Length() * transform.BlocksPerPixel / 2f;
        var guard = CellBlocks / 2.0;
        var half = SpanBlocks / 2.0;
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
        var deltaCellsX = deltaTexelsX / CellTexels;
        var deltaCellsZ = deltaTexelsZ / CellTexels;
        Array.Fill(_scratchCellVersions, UnfilledVersion);
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
                    _scratchCellVersions[(cellZ * cells) + cellX] =
                        _cellVersions[((int)sourceZ * cells) + (int)sourceX];
                }
            }
        }

        (_cellVersions, _scratchCellVersions) = (_scratchCellVersions, _cellVersions);
        _originX = newOriginX;
        _originZ = newOriginZ;
        _uploadPending = true;
        _forceNextUpload = true;
    }

    private void PollVersions(IExploredMapTileVersionSource? versionSource, double time)
    {
        if (versionSource is null || time - _lastVersionPollTime < VersionPollIntervalSeconds)
        {
            return;
        }

        _lastVersionPollTime = time;
        var mutationVersion = versionSource.MutationVersion;
        if (mutationVersion == _lastSeenMutationVersion)
        {
            return;
        }

        _lastSeenMutationVersion = mutationVersion;
        var cells = CellsPerSide;
        for (var cellZ = 0; cellZ < cells; cellZ++)
        {
            for (var cellX = 0; cellX < cells; cellX++)
            {
                var index = (cellZ * cells) + cellX;
                if (_cellVersions[index] == UnfilledVersion)
                {
                    continue;
                }

                if (_cellVersions[index] != GetCellVersion(versionSource, cellX, cellZ))
                {
                    _cellVersions[index] = UnfilledVersion;
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
        int texelBudget)
    {
        var texelsPerCell = CellTexels * CellTexels;
        IExploredMapReadSession? session = null;
        try
        {
            foreach (var index in _cellRefillOrder)
            {
                if (texelBudget < texelsPerCell)
                {
                    break;
                }

                if (_cellVersions[index] != UnfilledVersion)
                {
                    continue;
                }

                var cells = CellsPerSide;
                var cellX = index % cells;
                var cellZ = index / cells;
                var stamp = versionSource is null ? 0L : GetCellVersion(versionSource, cellX, cellZ);
                session ??= source.BeginReadSession();
                FillCell(session, cellX, cellZ);
                _cellVersions[index] = stamp;
                texelBudget -= texelsPerCell;
                _uploadPending = true;
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private void FillCell(IExploredMapReadSession session, int cellX, int cellZ)
    {
        var size = _textureSize;
        var texelBlocks = _texelBlocks;
        var aggregate = texelBlocks > 1 ? session as IExploredMapAggregateReadSession : null;
        var strength = _bakedShadingStrength;
        var startTexelX = cellX * CellTexels;
        var startTexelZ = cellZ * CellTexels;
        var cellWorldX = _originX + ((long)startTexelX * texelBlocks);
        var cellWorldZ = _originZ + ((long)startTexelZ * texelBlocks);
        var worldInRange = cellWorldX >= int.MinValue
            && cellWorldZ >= int.MinValue
            && cellWorldX + CellBlocks <= int.MaxValue
            && cellWorldZ + CellBlocks <= int.MaxValue;
        for (var z = 0; z < CellTexels; z++)
        {
            var rowIndex = ((startTexelZ + z) * size) + startTexelX;
            if (!worldInRange)
            {
                Array.Clear(_pixels, rowIndex, CellTexels);
                continue;
            }

            var worldZ = (int)(cellWorldZ + ((long)z * texelBlocks));
            for (var x = 0; x < CellTexels; x++)
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

    private bool ConsumeUploadRequest(double time)
    {
        if (!_uploadPending)
        {
            return false;
        }

        if (!_forceNextUpload && time - _lastUploadTime < UploadMinIntervalSeconds)
        {
            return false;
        }

        _uploadPending = false;
        _forceNextUpload = false;
        _lastUploadTime = time;
        return true;
    }

    private static int[] CreateCenterOutOrder(int cellsPerSide)
    {
        var order = Enumerable.Range(0, cellsPerSide * cellsPerSide).ToArray();
        var center = (cellsPerSide - 1) / 2f;
        return order
            .OrderBy(index =>
            {
                var x = (index % cellsPerSide) - center;
                var z = (index / cellsPerSide) - center;
                return (x * x) + (z * z);
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
