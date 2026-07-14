using SurvivalcraftTravelMap.Map;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class MinimapExplorationFootprintTests
{
    [Fact]
    public void Identity_stays_equal_when_movement_preserves_center_and_chunk_bounds()
    {
        var first = MinimapExplorationFootprintIdentity.Create(4f, 4f, 16, 1f);
        var moved = MinimapExplorationFootprintIdentity.Create(4.25f, 4.25f, 16, 1f);

        Assert.Equal(first, moved);
    }

    [Fact]
    public void Identity_changes_when_a_footprint_bound_crosses_a_chunk_without_changing_center()
    {
        var first = MinimapExplorationFootprintIdentity.Create(4f, 4f, 16, 1f);
        var crossed = MinimapExplorationFootprintIdentity.Create(8f, 8f, 16, 1f);

        Assert.Equal(first.CenterChunk, crossed.CenterChunk);
        Assert.NotEqual(first, crossed);
    }

    [Fact]
    public void Identity_changes_when_center_chunk_changes_inside_the_same_bounds()
    {
        var first = MinimapExplorationFootprintIdentity.Create(15.75f, 15.75f, 160, 0.7f);
        var crossed = MinimapExplorationFootprintIdentity.Create(16.25f, 16.25f, 160, 0.7f);

        Assert.Equal(first.MinimumChunk, crossed.MinimumChunk);
        Assert.Equal(first.MaximumChunk, crossed.MaximumChunk);
        Assert.NotEqual(first.CenterChunk, crossed.CenterChunk);
        Assert.NotEqual(first, crossed);
    }

    [Theory]
    [InlineData(64, 1f)]
    [InlineData(32, 2f)]
    public void Identity_changes_when_size_or_scale_changes_chunk_bounds(int sizePixels, float blocksPerPixel)
    {
        var first = MinimapExplorationFootprintIdentity.Create(4f, 4f, 32, 1f);
        var changed = MinimapExplorationFootprintIdentity.Create(4f, 4f, sizePixels, blocksPerPixel);

        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void Identity_uses_floor_semantics_across_negative_chunk_boundaries()
    {
        var first = MinimapExplorationFootprintIdentity.Create(-0.25f, -16.25f, 16, 1f);
        var moved = MinimapExplorationFootprintIdentity.Create(-0.75f, -16.75f, 16, 1f);
        var crossed = MinimapExplorationFootprintIdentity.Create(-8.25f, -8.25f, 16, 1f);

        Assert.Equal(new TerrainChunkCoordinate(-1, -2), first.CenterChunk);
        Assert.Equal(new TerrainChunkCoordinate(-1, -2), first.MinimumChunk);
        Assert.Equal(new TerrainChunkCoordinate(0, -1), first.MaximumChunk);
        Assert.Equal(first, moved);
        Assert.Equal(first.CenterChunk.X, crossed.CenterChunk.X);
        Assert.NotEqual(first.MinimumChunk.X, crossed.MinimumChunk.X);
    }

    [Fact]
    public void Footprint_created_from_identity_uses_its_exact_precomputed_chunks()
    {
        var identity = MinimapExplorationFootprintIdentity.Create(-7.75f, 23.25f, 32, 1.5f);

        var footprint = MinimapExplorationFootprint.Create(identity);

        Assert.Equal(identity.CenterChunk, footprint.CenterChunk);
        Assert.Equal(identity.MinimumChunk, footprint.MinimumChunk);
        Assert.Equal(identity.MaximumChunk, footprint.MaximumChunk);
    }

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
