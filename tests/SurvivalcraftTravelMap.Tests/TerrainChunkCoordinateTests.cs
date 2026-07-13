using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TerrainChunkCoordinateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(16, 1)]
    [InlineData(31, 1)]
    [InlineData(-1, -1)]
    [InlineData(-16, -1)]
    [InlineData(-17, -2)]
    [InlineData(int.MinValue, -134217728)]
    [InlineData(int.MaxValue, 134217727)]
    public void World_x_uses_mathematical_floor_division(int worldX, int expectedChunkX)
    {
        var chunk = TerrainChunkCoordinate.FromWorld(worldX, worldZ: 0);

        Assert.Equal(expectedChunkX, chunk.X);
        Assert.Equal(0, chunk.Z);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(16, 1)]
    [InlineData(31, 1)]
    [InlineData(-1, -1)]
    [InlineData(-16, -1)]
    [InlineData(-17, -2)]
    [InlineData(int.MinValue, -134217728)]
    [InlineData(int.MaxValue, 134217727)]
    public void World_z_uses_mathematical_floor_division(int worldZ, int expectedChunkZ)
    {
        var chunk = TerrainChunkCoordinate.FromWorld(worldX: 0, worldZ);

        Assert.Equal(0, chunk.X);
        Assert.Equal(expectedChunkZ, chunk.Z);
    }

    [Fact]
    public void Constants_describe_one_sixteen_by_sixteen_chunk()
    {
        Assert.Equal(16, TerrainChunkCoordinate.Size);
        Assert.Equal(256, TerrainChunkCoordinate.PixelCount);
    }

    [Fact]
    public void Origins_at_representable_limits_are_available()
    {
        var chunk = new TerrainChunkCoordinate(-134217728, 134217727);

        Assert.Equal(int.MinValue, chunk.OriginX);
        Assert.Equal(2147483632, chunk.OriginZ);
    }

    [Fact]
    public void Accessing_an_unrepresentable_x_origin_throws_checked_overflow()
    {
        var belowMinimum = new TerrainChunkCoordinate(-134217729, 0);
        var aboveMaximum = new TerrainChunkCoordinate(134217728, 0);

        Assert.Throws<OverflowException>(() =>
        {
            _ = belowMinimum.OriginX;
        });
        Assert.Throws<OverflowException>(() =>
        {
            _ = aboveMaximum.OriginX;
        });
    }

    [Fact]
    public void Accessing_an_unrepresentable_z_origin_throws_checked_overflow()
    {
        var belowMinimum = new TerrainChunkCoordinate(0, -134217729);
        var aboveMaximum = new TerrainChunkCoordinate(0, 134217728);

        Assert.Throws<OverflowException>(() =>
        {
            _ = belowMinimum.OriginZ;
        });
        Assert.Throws<OverflowException>(() =>
        {
            _ = aboveMaximum.OriginZ;
        });
    }
}
