namespace SurvivalcraftTravelMap.Map;

public readonly record struct TileCoordinate(int TileX, int TileZ, int LocalX, int LocalZ)
{
    public const int TileSize = 64;

    public static TileCoordinate FromWorld(int x, int z)
    {
        var tileX = FloorDivRem(x, out var localX);
        var tileZ = FloorDivRem(z, out var localZ);
        return new TileCoordinate(tileX, tileZ, localX, localZ);
    }

    private static int FloorDivRem(int value, out int remainder)
    {
        var quotient = Math.DivRem(value, TileSize, out remainder);
        if (remainder < 0)
        {
            quotient--;
            remainder += TileSize;
        }

        return quotient;
    }
}
