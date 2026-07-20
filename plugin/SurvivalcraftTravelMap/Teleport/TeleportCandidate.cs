namespace SurvivalcraftTravelMap.Teleport;

public readonly record struct TeleportCandidate(int X, int? Y, int Z)
{
    public const int SearchRadius = 8;

    private static readonly int[] VerticalOffsets =
        [0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8, -8];

    public static IEnumerable<TeleportCandidate> GenerateSurface(int x, int z)
    {
        foreach (var column in GenerateColumns(x, z))
        {
            yield return new TeleportCandidate(column.X, null, column.Z);
        }
    }

    public static IEnumerable<TeleportCandidate> GenerateWaypoint(int x, int y, int z)
    {
        foreach (var column in GenerateColumns(x, z))
        {
            foreach (var offset in VerticalOffsets)
            {
                var candidateY = (long)y + offset;
                if (candidateY is >= int.MinValue and <= int.MaxValue)
                {
                    yield return new TeleportCandidate(column.X, (int)candidateY, column.Z);
                }
            }
        }
    }

    private static IEnumerable<(int X, int Z)> GenerateColumns(int centerX, int centerZ)
    {
        var columns = new List<(int X, int Z, long DistanceSquared)>((SearchRadius * 2 + 1) * (SearchRadius * 2 + 1));
        for (var offsetX = -SearchRadius; offsetX <= SearchRadius; offsetX++)
        {
            for (var offsetZ = -SearchRadius; offsetZ <= SearchRadius; offsetZ++)
            {
                var candidateX = (long)centerX + offsetX;
                var candidateZ = (long)centerZ + offsetZ;
                if (candidateX is < int.MinValue or > int.MaxValue
                    || candidateZ is < int.MinValue or > int.MaxValue)
                {
                    continue;
                }

                var deltaX = (long)offsetX;
                var deltaZ = (long)offsetZ;
                columns.Add(((int)candidateX, (int)candidateZ, (deltaX * deltaX) + (deltaZ * deltaZ)));
            }
        }

        foreach (var column in columns
                     .OrderBy(column => column.DistanceSquared)
                     .ThenBy(column => column.X)
                     .ThenBy(column => column.Z))
        {
            yield return (column.X, column.Z);
        }
    }
}
