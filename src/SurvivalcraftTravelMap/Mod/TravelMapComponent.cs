using System.Numerics;
using Game;
using Game.NetWork;
using GameEntitySystem;
using SurvivalcraftTravelMap.Teleport;
using TemplatesDatabase;

namespace SurvivalcraftTravelMap.Mod;

public enum TravelMapWorkType
{
    Local,
    Server,
    Client,
}

public static class TravelMapRuntimePolicy
{
    public static bool CreatesUi(TravelMapWorkType workType) => workType is not TravelMapWorkType.Server;

    public static bool CreatesTeleportService(TravelMapWorkType workType) => workType is not TravelMapWorkType.Client;

    public static bool AllowsDirectPositionWrite(TravelMapWorkType workType) => workType == TravelMapWorkType.Local;
}

public sealed class TravelMapComponent : Component, IUpdateable
{
    private static int s_nextUpdateLocationId = -1_000_000;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private GameUpdateDispatcher? _dispatcher;
    private SurvivalcraftChunkLoader? _chunkLoader;

    internal ComponentPlayer Player { get; private set; } = null!;

    internal SubsystemTerrain Terrain { get; private set; } = null!;

    internal SubsystemTimeOfDay TimeOfDay { get; private set; } = null!;

    internal ComponentGui? Gui { get; private set; }

    internal SafeTeleportService? TeleportService { get; private set; }

    public TravelMapWorkType WorkType { get; private set; }

    public UpdateOrder UpdateOrder => UpdateOrder.Default;

    public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        var player = Entity.FindComponent<ComponentPlayer>(true)
            ?? throw new InvalidOperationException("TravelMapComponent must be attached to a player entity.");
        var playerBody = player.ComponentBody
            ?? throw new InvalidOperationException("The travel-map player does not have a body component.");
        Player = player;
        Terrain = Project.FindSubsystem<SubsystemTerrain>(true);
        TimeOfDay = Project.FindSubsystem<SubsystemTimeOfDay>(true);
        WorkType = ToTravelMapWorkType(CommonLib.WorkType);
        Gui = TravelMapRuntimePolicy.CreatesUi(WorkType)
            ? player.ComponentGui
            : null;

        _dispatcher = new GameUpdateDispatcher();
        if (!TravelMapRuntimePolicy.CreatesTeleportService(WorkType))
        {
            return;
        }

        var bodies = Project.FindSubsystem<SubsystemBodies>(true);
        var terrainAccess = new SurvivalcraftTerrainAccess(
            Terrain,
            playerBody,
            bodies,
            _dispatcher);
        var clock = new SurvivalcraftTeleportClock(_dispatcher);
        var updateLocationId = Interlocked.Decrement(ref s_nextUpdateLocationId);
        _chunkLoader = new SurvivalcraftChunkLoader(
            Terrain,
            updateLocationId,
            _dispatcher,
            clock);
        var playerMover = new SurvivalcraftPlayerMover(Player, _dispatcher);
        TeleportService = new SafeTeleportService(
            terrainAccess,
            _chunkLoader,
            playerMover,
            terrainAccess,
            clock);
    }

    public void Update(float dt) => _dispatcher?.Pump();

    public async Task<TeleportResult> TeleportToSurfaceAsync(
        int x,
        int z,
        CancellationToken cancellationToken)
    {
        var service = GetLocalTeleportService();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        return await service.TeleportToSurfaceAsync(x, z, linkedCancellation.Token);
    }

    public async Task<TeleportResult> TeleportToWaypointAsync(
        Vector3 xyz,
        CancellationToken cancellationToken)
    {
        var service = GetLocalTeleportService();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        return await service.TeleportToWaypointAsync(xyz, linkedCancellation.Token);
    }

    public override void OnEntityRemoved()
    {
        _lifetimeCancellation.Cancel();
        _chunkLoader?.Dispose();
        _dispatcher?.Dispose();
        _lifetimeCancellation.Dispose();
        base.OnEntityRemoved();
    }

    private SafeTeleportService GetLocalTeleportService()
    {
        if (!TravelMapRuntimePolicy.AllowsDirectPositionWrite(WorkType) || TeleportService is null)
        {
            throw new InvalidOperationException(
                "Direct player movement is only available in a local world. Multiplayer travel requires server authorization.");
        }

        return TeleportService;
    }

    private static TravelMapWorkType ToTravelMapWorkType(WorkType workType) => workType switch
    {
        Game.NetWork.WorkType.Local => TravelMapWorkType.Local,
        Game.NetWork.WorkType.Server => TravelMapWorkType.Server,
        Game.NetWork.WorkType.Client => TravelMapWorkType.Client,
        _ => throw new InvalidOperationException($"Unsupported Survivalcraft work type: {workType}."),
    };
}
