namespace SurvivalcraftTravelMap.Teleport;

public static class TeleportBlockSafetyPolicy
{
    public static bool IsHarmful(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Lava or
        TeleportBlockKind.Fire or
        TeleportBlockKind.Cactus or
        TeleportBlockKind.Spikes or
        TeleportBlockKind.Damaging;

    public static bool IsStableSupport(TeleportBlockKind kind) => kind is
        TeleportBlockKind.SafeSolid or
        TeleportBlockKind.Leaves or
        TeleportBlockKind.Falling;

    public static bool IsWater(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Water or
        TeleportBlockKind.Fluid;

    public static bool IsFeetPassable(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Air or
        TeleportBlockKind.Passable or
        TeleportBlockKind.Water or
        TeleportBlockKind.Fluid;

    public static bool IsHeadBreathable(TeleportBlockKind kind) => kind is
        TeleportBlockKind.Air or
        TeleportBlockKind.Passable;
}
