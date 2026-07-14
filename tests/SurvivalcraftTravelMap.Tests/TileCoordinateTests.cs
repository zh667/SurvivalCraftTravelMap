using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TileCoordinateTests
{
    [Theory]
    [InlineData(-1, -1, 63)]
    [InlineData(-64, -1, 0)]
    [InlineData(-65, -2, 63)]
    public void Negative_world_coordinates_use_floor_division(int world, int tile, int local)
    {
        var result = TileCoordinate.FromWorld(world, world);

        Assert.Equal(tile, result.TileX);
        Assert.Equal(local, result.LocalX);
    }
}
