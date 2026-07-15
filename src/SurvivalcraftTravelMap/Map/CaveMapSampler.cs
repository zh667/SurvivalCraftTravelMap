namespace SurvivalcraftTravelMap.Map;

public static class CaveLayer
{
    public const int MinimumY = 1;
    public const int MaximumY = 254;
    public const int ProjectionRadius = 8;
    public const int MarkerVerticalRange = ProjectionRadius;

    public static int CenterForY(float y)
    {
        var finite = float.IsFinite(y) ? y : 64f;
        return ClampCenter((int)MathF.Floor(finite));
    }

    public static int ClampCenter(int y) => Math.Clamp(y, MinimumY, MaximumY);
}

public sealed class CaveMapSampler
{
    public const int VerticalScanRadius = CaveLayer.ProjectionRadius;

    public static Rgba32 HiddenRockColor { get; } = new(0x1B, 0x26, 0x28, 0xFF);

    private readonly ITerrainMapSource _terrain;
    private readonly TerrainMapSampler _colors;

    public CaveMapSampler(ITerrainMapSource terrain, TerrainMapSampler colors)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        _colors = colors ?? throw new ArgumentNullException(nameof(colors));
    }

    public bool TrySampleChunk(
        TerrainChunkCoordinate chunk,
        int feetY,
        Span<Rgba32> destination,
        Span<byte> heightShades)
    {
        if (destination.Length != TerrainChunkCoordinate.PixelCount)
        {
            throw new ArgumentException(
                $"Destination length must be exactly {TerrainChunkCoordinate.PixelCount} pixels.",
                nameof(destination));
        }

        if (heightShades.Length != TerrainChunkCoordinate.PixelCount)
        {
            throw new ArgumentException(
                $"Height-shade destination must be exactly {TerrainChunkCoordinate.PixelCount} values.",
                nameof(heightShades));
        }

        feetY = CaveLayer.ClampCenter(feetY);
        if (!_terrain.IsChunkSurfaceReady(chunk))
        {
            return false;
        }

        const int gridSize = TerrainChunkCoordinate.Size + 2;
        Span<short> walkableY = stackalloc short[gridSize * gridSize];
        Span<short> fluidY = stackalloc short[gridSize * gridSize];
        walkableY.Clear();
        fluidY.Clear();
        for (var localZ = -1; localZ <= TerrainChunkCoordinate.Size; localZ++)
        {
            for (var localX = -1; localX <= TerrainChunkCoordinate.Size; localX++)
            {
                var worldX = chunk.OriginX + localX;
                var worldZ = chunk.OriginZ + localZ;
                if (!_terrain.IsColumnReady(worldX, worldZ))
                {
                    continue;
                }

                var gridIndex = ((localZ + 1) * gridSize) + localX + 1;
                walkableY[gridIndex] = checked((short)FindNearestWalkableY(worldX, feetY, worldZ));
                fluidY[gridIndex] = checked((short)FindNearestFluidY(worldX, feetY, worldZ));
            }
        }

        for (var localZ = 0; localZ < TerrainChunkCoordinate.Size; localZ++)
        {
            for (var localX = 0; localX < TerrainChunkCoordinate.Size; localX++)
            {
                var worldX = chunk.OriginX + localX;
                var worldZ = chunk.OriginZ + localZ;
                var gridIndex = ((localZ + 1) * gridSize) + localX + 1;
                var pixelIndex = (localZ * TerrainChunkCoordinate.Size) + localX;
                var sampledY = walkableY[gridIndex];
                if (sampledY != 0)
                {
                    var floorContent = _terrain.GetContent(worldX, sampledY - 1, worldZ);
                    destination[pixelIndex] = _colors.SampleContent(
                        floorContent,
                        worldX,
                        sampledY - 1,
                        worldZ);
                    heightShades[pixelIndex] = TerrainHeightShading.Encode(
                        HeightFactor(sampledY, feetY, baseFactor: 0.88f));
                    continue;
                }

                sampledY = fluidY[gridIndex];
                if (sampledY != 0)
                {
                    destination[pixelIndex] = _colors.SampleContent(
                        _terrain.GetContent(worldX, sampledY, worldZ),
                        worldX,
                        sampledY,
                        worldZ);
                    heightShades[pixelIndex] = TerrainHeightShading.Encode(
                        HeightFactor(sampledY, feetY, baseFactor: 0.72f));
                    continue;
                }

                var neighboringY = FindNearestNeighborY(walkableY, gridIndex, gridSize, feetY);
                destination[pixelIndex] = neighboringY != 0
                    ? SampleCaveWall(worldX, neighboringY, worldZ)
                    : HiddenRockColor;
                heightShades[pixelIndex] = neighboringY != 0
                    ? TerrainHeightShading.Encode(HeightFactor(neighboringY, feetY, baseFactor: 0.56f))
                    : TerrainHeightShading.Neutral;
            }
        }

        return true;
    }

    private bool IsWalkable(int x, int feetY, int z) =>
        _terrain.IsCollidableCell(x, feetY - 1, z)
        && _terrain.IsPassableCell(x, feetY, z)
        && !_terrain.IsFluidCell(x, feetY, z)
        && _terrain.IsPassableCell(x, feetY + 1, z)
        && !_terrain.IsFluidCell(x, feetY + 1, z);

    private int FindNearestWalkableY(int x, int selectedY, int z)
    {
        if (IsWalkable(x, selectedY, z))
        {
            return selectedY;
        }

        for (var offset = 1; offset <= VerticalScanRadius; offset++)
        {
            var below = selectedY - offset;
            if (below >= CaveLayer.MinimumY && IsWalkable(x, below, z))
            {
                return below;
            }

            var above = selectedY + offset;
            if (above <= CaveLayer.MaximumY && IsWalkable(x, above, z))
            {
                return above;
            }
        }

        return 0;
    }

    private int FindNearestFluidY(int x, int selectedY, int z)
    {
        if (_terrain.IsFluidCell(x, selectedY, z))
        {
            return selectedY;
        }

        for (var offset = 1; offset <= VerticalScanRadius; offset++)
        {
            var below = selectedY - offset;
            if (below >= CaveLayer.MinimumY && _terrain.IsFluidCell(x, below, z))
            {
                return below;
            }

            var above = selectedY + offset;
            if (above <= CaveLayer.MaximumY && _terrain.IsFluidCell(x, above, z))
            {
                return above;
            }
        }

        return 0;
    }

    private Rgba32 SampleCaveWall(int x, int y, int z)
    {
        var content = _terrain.GetContent(x, y, z);
        if (content == 0 || _terrain.IsFluidCell(x, y, z))
        {
            content = _terrain.GetContent(x, y - 1, z);
            y--;
        }

        var color = _colors.SampleContent(content, x, y, z);
        if (color.A == 0)
        {
            return HiddenRockColor;
        }

        return color;
    }

    private static int FindNearestNeighborY(
        ReadOnlySpan<short> walkableY,
        int center,
        int stride,
        int selectedY)
    {
        var nearestY = 0;
        var nearestDistance = int.MaxValue;
        for (var z = -1; z <= 1; z++)
        {
            for (var x = -1; x <= 1; x++)
            {
                if (x == 0 && z == 0)
                {
                    continue;
                }

                var candidateY = walkableY[center + (z * stride) + x];
                var distance = Math.Abs(candidateY - selectedY);
                if (candidateY != 0 && distance < nearestDistance)
                {
                    nearestY = candidateY;
                    nearestDistance = distance;
                }
            }
        }

        return nearestY;
    }

    private static float HeightFactor(int sampledY, int selectedY, float baseFactor) =>
        Math.Clamp(
            baseFactor + ((sampledY - selectedY) * 0.018f),
            0.42f,
            1.08f);
}
