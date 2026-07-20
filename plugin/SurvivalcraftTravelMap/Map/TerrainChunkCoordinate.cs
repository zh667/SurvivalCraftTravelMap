namespace SurvivalcraftTravelMap.Map;

public readonly record struct TerrainChunkCoordinate(int X, int Z)
{
    public const int Size = 16;
    public const int PixelCount = Size * Size;

    public int OriginX => checked(X * Size);

    public int OriginZ => checked(Z * Size);

    public static TerrainChunkCoordinate FromWorld(int worldX, int worldZ)
    {
        return new TerrainChunkCoordinate(
            FloorDivideBySize(worldX),
            FloorDivideBySize(worldZ));
    }

    private static int FloorDivideBySize(int value)
    {
        var quotient = Math.DivRem(value, Size, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
