using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MinimapExplorationFootprintTests
{
    [Theory]
    [InlineData(256, 0.5f, 8, 8)]
    [InlineData(256, 1f, 16, 16)]
    public void Footprint_covers_every_chunk_intersecting_the_minimap_square(
        int size,
        float blocksPerPixel,
        int minimumChunksWide,
        int minimumChunksHigh)
    {
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, size, blocksPerPixel);

        Assert.True(footprint.MaximumChunk.X - footprint.MinimumChunk.X + 1 >= minimumChunksWide);
        Assert.True(footprint.MaximumChunk.Z - footprint.MinimumChunk.Z + 1 >= minimumChunksHigh);
        Assert.Equal(footprint.CenterChunk, footprint.ChunksNearestFirst[0]);
        Assert.Equal(footprint.ChunksNearestFirst.Count, footprint.ChunksNearestFirst.Distinct().Count());
    }

    [Theory]
    [InlineData(-0.25f, -1)]
    [InlineData(-16.25f, -2)]
    [InlineData(15.75f, 0)]
    [InlineData(16.25f, 1)]
    public void Center_chunk_uses_floor_coordinates_at_negative_boundaries(float coordinate, int expected)
    {
        var footprint = MinimapExplorationFootprint.Create(coordinate, coordinate, 160, 0.5f);

        Assert.Equal(new TerrainChunkCoordinate(expected, expected), footprint.CenterChunk);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Nonfinite_player_or_scale_values_are_rejected(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinimapExplorationFootprint.Create(value, 0f, 192, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinimapExplorationFootprint.Create(0f, 0f, 192, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_pixel_sizes_are_rejected(int sizePixels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinimapExplorationFootprint.Create(0f, 0f, sizePixels, 1f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Nonpositive_blocks_per_pixel_values_are_rejected(float blocksPerPixel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinimapExplorationFootprint.Create(0f, 0f, 192, blocksPerPixel));
    }

    [Fact]
    public void Chunks_are_ordered_by_squared_distance_then_x_then_z()
    {
        var footprint = MinimapExplorationFootprint.Create(8f, 8f, 64, 1f);
        var center = footprint.CenterChunk;
        var expected = footprint.ChunksNearestFirst
            .OrderBy(chunk => DistanceSquared(chunk, center))
            .ThenBy(chunk => chunk.X)
            .ThenBy(chunk => chunk.Z)
            .ToArray();

        Assert.Equal(expected, footprint.ChunksNearestFirst);
    }

    private static long DistanceSquared(TerrainChunkCoordinate left, TerrainChunkCoordinate right)
    {
        var dx = (long)left.X - right.X;
        var dz = (long)left.Z - right.Z;
        return dx * dx + dz * dz;
    }
}
