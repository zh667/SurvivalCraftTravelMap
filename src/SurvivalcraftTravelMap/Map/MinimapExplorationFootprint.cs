namespace SurvivalcraftTravelMap.Map;

public sealed class MinimapExplorationFootprint
{
    private MinimapExplorationFootprint(
        TerrainChunkCoordinate centerChunk,
        TerrainChunkCoordinate minimumChunk,
        TerrainChunkCoordinate maximumChunk,
        IReadOnlyList<TerrainChunkCoordinate> chunksNearestFirst)
    {
        CenterChunk = centerChunk;
        MinimumChunk = minimumChunk;
        MaximumChunk = maximumChunk;
        ChunksNearestFirst = chunksNearestFirst;
    }

    public TerrainChunkCoordinate CenterChunk { get; }
    public TerrainChunkCoordinate MinimumChunk { get; }
    public TerrainChunkCoordinate MaximumChunk { get; }
    public IReadOnlyList<TerrainChunkCoordinate> ChunksNearestFirst { get; }

    public static MinimapExplorationFootprint Create(
        float playerX,
        float playerZ,
        int sizePixels,
        float blocksPerPixel)
    {
        if (!float.IsFinite(playerX) || !float.IsFinite(playerZ))
            throw new ArgumentOutOfRangeException(nameof(playerX));
        if (sizePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizePixels));
        if (!float.IsFinite(blocksPerPixel) || blocksPerPixel <= 0f)
            throw new ArgumentOutOfRangeException(nameof(blocksPerPixel));

        var halfExtent = (double)sizePixels * blocksPerPixel / 2d;
        var minimumWorldX = CheckedFloor(playerX - halfExtent);
        var minimumWorldZ = CheckedFloor(playerZ - halfExtent);
        var maximumWorldX = CheckedCeilingMinusOne(playerX + halfExtent);
        var maximumWorldZ = CheckedCeilingMinusOne(playerZ + halfExtent);
        var center = TerrainChunkCoordinate.FromWorld(CheckedFloor(playerX), CheckedFloor(playerZ));
        var minimum = TerrainChunkCoordinate.FromWorld(minimumWorldX, minimumWorldZ);
        var maximum = TerrainChunkCoordinate.FromWorld(maximumWorldX, maximumWorldZ);
        var chunks = Enumerate(minimum, maximum, center);
        return new MinimapExplorationFootprint(center, minimum, maximum, chunks);
    }

    private static int CheckedFloor(double value) => checked((int)Math.Floor(value));
    private static int CheckedCeilingMinusOne(double value) => checked((int)Math.Ceiling(value) - 1);

    private static IReadOnlyList<TerrainChunkCoordinate> Enumerate(
        TerrainChunkCoordinate minimum,
        TerrainChunkCoordinate maximum,
        TerrainChunkCoordinate center)
    {
        var chunks = new List<TerrainChunkCoordinate>();
        for (var z = (long)minimum.Z; z <= maximum.Z; z++)
        for (var x = (long)minimum.X; x <= maximum.X; x++)
            chunks.Add(new TerrainChunkCoordinate(checked((int)x), checked((int)z)));

        return chunks
            .OrderBy(chunk => DistanceSquared(chunk, center))
            .ThenBy(chunk => chunk.X)
            .ThenBy(chunk => chunk.Z)
            .ToArray();
    }

    private static long DistanceSquared(TerrainChunkCoordinate left, TerrainChunkCoordinate right)
    {
        var dx = (long)left.X - right.X;
        var dz = (long)left.Z - right.Z;
        return dx * dx + dz * dz;
    }
}
