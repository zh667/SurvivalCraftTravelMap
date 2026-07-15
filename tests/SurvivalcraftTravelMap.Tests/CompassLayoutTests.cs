using System.Numerics;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class CompassLayoutTests
{
    [Fact]
    public void North_up_places_cardinal_directions_on_matching_edges()
    {
        var labels = CompassLayout.Create(
            new Vector2(160f),
            rotationRadians: 0f,
            CompassBoundaryShape.RoundedSquare,
            showNorth: true,
            showOtherDirections: true,
            fontScale: 1f,
            bottomReservedHeight: 0f);

        Assert.Collection(
            labels,
            north => AssertLabel(north, CompassDirection.North, new Vector2(80f, 12f)),
            east => AssertLabel(east, CompassDirection.East, new Vector2(148f, 80f)),
            south => AssertLabel(south, CompassDirection.South, new Vector2(80f, 148f)),
            west => AssertLabel(west, CompassDirection.West, new Vector2(12f, 80f)));
    }

    [Fact]
    public void Heading_up_rotates_world_directions_with_the_shared_map_rotation()
    {
        var labels = CompassLayout.Create(
            new Vector2(160f),
            rotationRadians: -MathF.PI / 2f,
            CompassBoundaryShape.RoundedSquare,
            showNorth: true,
            showOtherDirections: true,
            fontScale: 1f,
            bottomReservedHeight: 0f);

        AssertLabel(labels[0], CompassDirection.North, new Vector2(12f, 80f));
        AssertLabel(labels[1], CompassDirection.East, new Vector2(80f, 12f));
        AssertLabel(labels[2], CompassDirection.South, new Vector2(148f, 80f));
        AssertLabel(labels[3], CompassDirection.West, new Vector2(80f, 148f));
    }

    [Fact]
    public void Independent_direction_choices_cover_every_visible_combination()
    {
        var northOnly = CompassLayout.Create(
            new Vector2(192f),
            rotationRadians: 0.42f,
            CompassBoundaryShape.Circle,
            showNorth: true,
            showOtherDirections: false,
            fontScale: 1f,
            bottomReservedHeight: 0f);
        var othersOnly = CompassLayout.Create(
            new Vector2(192f),
            rotationRadians: 0.42f,
            CompassBoundaryShape.Circle,
            showNorth: false,
            showOtherDirections: true,
            fontScale: 1f,
            bottomReservedHeight: 0f);
        var hidden = CompassLayout.Create(
            new Vector2(192f),
            rotationRadians: 0.42f,
            CompassBoundaryShape.Circle,
            showNorth: false,
            showOtherDirections: false,
            fontScale: 1f,
            bottomReservedHeight: 0f);

        var label = Assert.Single(northOnly);
        Assert.Equal(CompassDirection.North, label.Direction);
        Assert.True(label.IsNorth);
        Assert.Equal(
            [CompassDirection.East, CompassDirection.South, CompassDirection.West],
            othersOnly.Select(item => item.Direction));
        Assert.Empty(hidden);
    }

    [Theory]
    [InlineData(160f)]
    [InlineData(192f)]
    [InlineData(256f)]
    [InlineData(320f)]
    [InlineData(384f)]
    public void Every_shape_and_supported_size_keeps_labels_inside_the_map(float size)
    {
        foreach (var shape in Enum.GetValues<CompassBoundaryShape>())
        {
            for (var heading = 0; heading < 16; heading++)
            {
                var labels = CompassLayout.Create(
                    new Vector2(size),
                    heading * MathF.PI / 8f,
                    shape,
                    showNorth: true,
                    showOtherDirections: true,
                    fontScale: 2f,
                    bottomReservedHeight: 18f);

                Assert.Equal(4, labels.Count);
                Assert.All(labels, label =>
                {
                    Assert.InRange(label.Position.X, 0f, size);
                    Assert.InRange(label.Position.Y, 0f, size - 18f);
                });
            }
        }
    }

    [Fact]
    public void Coordinate_strip_reservation_moves_the_south_label_above_the_strip()
    {
        var south = CompassLayout.Create(
            new Vector2(160f),
            rotationRadians: 0f,
            CompassBoundaryShape.Square,
            showNorth: true,
            showOtherDirections: true,
            fontScale: 1f,
            bottomReservedHeight: 18f)[2];

        Assert.Equal(CompassDirection.South, south.Direction);
        Assert.InRange(south.Position.Y, 0f, 130f);
    }

    private static void AssertLabel(
        CompassLabel label,
        CompassDirection direction,
        Vector2 expected)
    {
        Assert.Equal(direction, label.Direction);
        Assert.InRange(label.Position.X, expected.X - 0.001f, expected.X + 0.001f);
        Assert.InRange(label.Position.Y, expected.Y - 0.001f, expected.Y + 0.001f);
    }
}
