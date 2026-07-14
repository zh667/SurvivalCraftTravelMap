using System.Numerics;
using Game;

namespace SurvivalcraftTravelMap.Teleport;

public enum SurvivalcraftBlockHazard
{
    None,
    Lava,
    Fire,
    Cactus,
    Spikes,
}

public readonly record struct SurvivalcraftBlockMetadata(
    bool IsAir = false,
    bool IsCollidable = false,
    bool IsFluid = false,
    bool IsLeaves = false,
    bool IsFalling = false,
    bool IsDamaging = false,
    SurvivalcraftBlockHazard Hazard = SurvivalcraftBlockHazard.None);

public interface ISurvivalcraftTerrainFacade
{
    bool IsColumnInWorld(int x, int z);

    bool IsCellInWorld(int x, int y, int z);

    int GetSurfaceHeight(int x, int z);

    SurvivalcraftBlockMetadata GetBlockMetadata(int x, int y, int z);

    bool HasBlockingEntityCollision(Vector3 feetPosition, object excludedBody);
}

public sealed class SurvivalcraftTerrainAccess : ITerrainAccess, IEntityCollisionQuery
{
    private readonly ISurvivalcraftTerrainFacade _facade;
    private readonly object _teleportedPlayerBody;

    public SurvivalcraftTerrainAccess(ISurvivalcraftTerrainFacade facade, object teleportedPlayerBody)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(teleportedPlayerBody);
        _facade = facade;
        _teleportedPlayerBody = teleportedPlayerBody;
    }

    public SurvivalcraftTerrainAccess(
        SubsystemTerrain terrain,
        ComponentBody teleportedPlayerBody,
        SubsystemBodies bodies,
        GameUpdateDispatcher dispatcher)
        : this(
            new SurvivalcraftTerrainFacade(terrain, teleportedPlayerBody, bodies, dispatcher),
            teleportedPlayerBody)
    {
    }

    public bool IsColumnInWorld(int x, int z) => _facade.IsColumnInWorld(x, z);

    public bool IsCellInWorld(int x, int y, int z) => _facade.IsCellInWorld(x, y, z);

    public int GetSurfaceHeight(int x, int z) => _facade.GetSurfaceHeight(x, z);

    public TeleportBlockKind GetBlockKind(int x, int y, int z) =>
        Classify(_facade.GetBlockMetadata(x, y, z));

    public bool HasBlockingCollisionExcludingPlayer(Vector3 feetPosition) =>
        _facade.HasBlockingEntityCollision(feetPosition, _teleportedPlayerBody);

    private static TeleportBlockKind Classify(SurvivalcraftBlockMetadata metadata) => metadata.Hazard switch
    {
        SurvivalcraftBlockHazard.Lava => TeleportBlockKind.Lava,
        SurvivalcraftBlockHazard.Fire => TeleportBlockKind.Fire,
        SurvivalcraftBlockHazard.Cactus => TeleportBlockKind.Cactus,
        SurvivalcraftBlockHazard.Spikes => TeleportBlockKind.Spikes,
        _ when metadata.IsDamaging => TeleportBlockKind.Damaging,
        _ when metadata.IsAir => TeleportBlockKind.Air,
        _ when metadata.IsFluid => TeleportBlockKind.Fluid,
        _ when metadata.IsLeaves => TeleportBlockKind.Leaves,
        _ when metadata.IsFalling => TeleportBlockKind.Falling,
        _ when metadata.IsCollidable => TeleportBlockKind.SafeSolid,
        _ => TeleportBlockKind.Passable,
    };
}

internal sealed class SurvivalcraftTerrainFacade : ISurvivalcraftTerrainFacade
{
    private readonly SubsystemTerrain _terrain;
    private readonly ComponentBody _playerBody;
    private readonly SubsystemBodies _bodies;
    private readonly GameUpdateDispatcher _dispatcher;

    internal SurvivalcraftTerrainFacade(
        SubsystemTerrain terrain,
        ComponentBody playerBody,
        SubsystemBodies bodies,
        GameUpdateDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(playerBody);
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _terrain = terrain;
        _playerBody = playerBody;
        _bodies = bodies;
        _dispatcher = dispatcher;
    }

    public bool IsColumnInWorld(int x, int z) =>
        _dispatcher.Invoke(() => _terrain.Terrain.IsCellValid(x, 0, z));

    public bool IsCellInWorld(int x, int y, int z) =>
        _dispatcher.Invoke(() => _terrain.Terrain.IsCellValid(x, y, z));

    public int GetSurfaceHeight(int x, int z) =>
        _dispatcher.Invoke(() => _terrain.Terrain.GetTopHeight(x, z));

    public SurvivalcraftBlockMetadata GetBlockMetadata(int x, int y, int z) => _dispatcher.Invoke(() =>
    {
        var value = _terrain.Terrain.GetCellValue(x, y, z);
        var contents = Terrain.ExtractContents(value);
        var block = BlocksManager.Blocks[contents];
        var hazard = block switch
        {
            MagmaBlock => SurvivalcraftBlockHazard.Lava,
            FireBlock => SurvivalcraftBlockHazard.Fire,
            CactusBlock => SurvivalcraftBlockHazard.Cactus,
            SpikedPlankBlock => SurvivalcraftBlockHazard.Spikes,
            _ => SurvivalcraftBlockHazard.None,
        };

        return new SurvivalcraftBlockMetadata(
            IsAir: contents == 0 || block is AirBlock,
            IsCollidable: block.IsCollidable_(value),
            IsFluid: block is FluidBlock,
            IsLeaves: block is LeavesBlock or FallenLeavesBlock,
            IsFalling: block is SandBlock or GravelBlock,
            IsDamaging: block.ShouldAvoid(value) || block.KillsWhenStuck || block.DefaultHeat > 0f,
            Hazard: hazard);
    });

    public bool HasBlockingEntityCollision(Vector3 feetPosition, object excludedBody) =>
        _dispatcher.Invoke(() => HasBlockingEntityCollisionOnUpdateThread(feetPosition, excludedBody));

    private bool HasBlockingEntityCollisionOnUpdateThread(Vector3 feetPosition, object excludedBody)
    {
        var boxSize = _playerBody.BoxSize;
        var candidate = new Engine.BoundingBox(
            feetPosition.X - boxSize.X / 2f,
            feetPosition.Y,
            feetPosition.Z - boxSize.Z / 2f,
            feetPosition.X + boxSize.X / 2f,
            feetPosition.Y + boxSize.Y,
            feetPosition.Z + boxSize.Z / 2f);
        foreach (var body in _bodies.Bodies)
        {
            if (!ReferenceEquals(body, excludedBody) && body.BoundingBox.Intersection(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
