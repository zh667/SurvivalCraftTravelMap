using SurvivalcraftTravelMap.Teleport;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TeleportBlockSafetyPolicyTests
{
    [Theory]
    [InlineData(TeleportBlockKind.SafeSolid)]
    [InlineData(TeleportBlockKind.Leaves)]
    [InlineData(TeleportBlockKind.Falling)]
    public void Ordinary_collidable_ground_is_stable_support(TeleportBlockKind kind) =>
        Assert.True(TeleportBlockSafetyPolicy.IsStableSupport(kind));

    [Theory]
    [InlineData(TeleportBlockKind.Air)]
    [InlineData(TeleportBlockKind.Passable)]
    [InlineData(TeleportBlockKind.Water)]
    [InlineData(TeleportBlockKind.Fluid)]
    public void Harmless_noncollidable_content_is_feet_passable(TeleportBlockKind kind) =>
        Assert.True(TeleportBlockSafetyPolicy.IsFeetPassable(kind));

    [Theory]
    [InlineData(TeleportBlockKind.Air, true)]
    [InlineData(TeleportBlockKind.Passable, true)]
    [InlineData(TeleportBlockKind.Water, false)]
    [InlineData(TeleportBlockKind.Fluid, false)]
    public void Head_must_remain_breathable(TeleportBlockKind kind, bool expected) =>
        Assert.Equal(expected, TeleportBlockSafetyPolicy.IsHeadBreathable(kind));

    [Fact]
    public void Only_explicit_hazards_and_damaging_blocks_are_harmful()
    {
        TeleportBlockKind[] harmful =
        [
            TeleportBlockKind.Lava,
            TeleportBlockKind.Fire,
            TeleportBlockKind.Cactus,
            TeleportBlockKind.Spikes,
            TeleportBlockKind.Damaging,
        ];

        foreach (TeleportBlockKind kind in Enum.GetValues<TeleportBlockKind>())
        {
            Assert.Equal(harmful.Contains(kind), TeleportBlockSafetyPolicy.IsHarmful(kind));
        }
    }

    [Theory]
    [InlineData(TeleportBlockKind.Water)]
    [InlineData(TeleportBlockKind.Fluid)]
    public void Water_semantics_include_both_water_kinds(TeleportBlockKind kind) =>
        Assert.True(TeleportBlockSafetyPolicy.IsWater(kind));
}
