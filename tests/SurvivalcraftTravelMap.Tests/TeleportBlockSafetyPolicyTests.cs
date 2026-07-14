using SurvivalcraftTravelMap.Teleport;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TeleportBlockSafetyPolicyTests
{
    public static TheoryData<TeleportBlockKind, bool, bool, bool, bool> SafetySemantics => new()
    {
        { TeleportBlockKind.Air, false, false, true, true },
        { TeleportBlockKind.Passable, false, false, true, true },
        { TeleportBlockKind.SafeSolid, true, false, false, false },
        { TeleportBlockKind.Lava, false, false, false, false },
        { TeleportBlockKind.Fire, false, false, false, false },
        { TeleportBlockKind.Cactus, false, false, false, false },
        { TeleportBlockKind.Spikes, false, false, false, false },
        { TeleportBlockKind.Water, false, true, true, false },
        { TeleportBlockKind.Fluid, false, true, true, false },
        { TeleportBlockKind.Leaves, true, false, false, false },
        { TeleportBlockKind.Falling, true, false, false, false },
        { TeleportBlockKind.Damaging, false, false, false, false },
    };

    [Theory]
    [MemberData(nameof(SafetySemantics))]
    public void Every_block_kind_has_exact_safety_semantics(
        TeleportBlockKind kind,
        bool stableSupport,
        bool water,
        bool feetPassable,
        bool headBreathable)
    {
        Assert.Equal(stableSupport, TeleportBlockSafetyPolicy.IsStableSupport(kind));
        Assert.Equal(water, TeleportBlockSafetyPolicy.IsWater(kind));
        Assert.Equal(feetPassable, TeleportBlockSafetyPolicy.IsFeetPassable(kind));
        Assert.Equal(headBreathable, TeleportBlockSafetyPolicy.IsHeadBreathable(kind));
    }

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
}
