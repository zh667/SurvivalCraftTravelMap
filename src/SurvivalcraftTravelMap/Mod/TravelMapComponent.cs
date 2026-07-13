using System.Numerics;
using Game;
using Game.NetWork;
using GameEntitySystem;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Persistence;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.Teleport;
using SurvivalcraftTravelMap.UI;
using SurvivalcraftTravelMap.Waypoints;
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

    public static void CleanupRuntime(
        Action cancelLifetime,
        Action releaseChunks,
        Action disposeDispatcher,
        Action finalCleanup)
    {
        ArgumentNullException.ThrowIfNull(cancelLifetime);
        ArgumentNullException.ThrowIfNull(releaseChunks);
        ArgumentNullException.ThrowIfNull(disposeDispatcher);
        ArgumentNullException.ThrowIfNull(finalCleanup);
        try
        {
            cancelLifetime();
        }
        finally
        {
            try
            {
                releaseChunks();
            }
            finally
            {
                try
                {
                    disposeDispatcher();
                }
                finally
                {
                    finalCleanup();
                }
            }
        }
    }
}

public sealed class TravelMapComponent : Component, IUpdateable
{
    private static int s_nextUpdateLocationId = -1_000_000;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TravelMapUiController _uiController = new();
    private GameUpdateDispatcher? _dispatcher;
    private SurvivalcraftChunkLoader? _chunkLoader;
    private TravelMapSettingsStore? _settingsStore;
    private TravelMapSettings? _settings;
    private ExplorationTileStore? _tileStore;
    private ExplorationRecorder? _explorationRecorder;
    private WaypointRepository? _waypointRepository;
    private IReadOnlyList<Waypoint> _waypoints = Array.Empty<Waypoint>();
    private MiniMapRenderer? _miniMap;
    private TravelMapDialog? _largeMapDialog;
    private TrackedUiActionRunner? _uiActions;
    private Task? _flushTask;
    private int _lastRecordedX = int.MinValue;
    private int _lastRecordedZ = int.MinValue;
    private float _stationaryRecordElapsed;
    private float _flushElapsed;

    internal ComponentPlayer Player { get; private set; } = null!;

    internal SubsystemTerrain Terrain { get; private set; } = null!;

    internal SubsystemTimeOfDay TimeOfDay { get; private set; } = null!;

    internal ComponentGui? Gui { get; private set; }

    internal SafeTeleportService? TeleportService { get; private set; }

    public TravelMapWorkType WorkType { get; private set; }

    public Action<TravelMapClientTravelCommand>? ClientTravelCommand { get; set; }

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
        if (TravelMapRuntimePolicy.CreatesTeleportService(WorkType))
        {
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

        if (TravelMapRuntimePolicy.CreatesUi(WorkType) && player.PlayerData.IsMainPlayer)
        {
            InitializeUi();
        }
    }

    public void Update(float dt)
    {
        _dispatcher?.Pump();
        if (_miniMap is null || _settings is null)
        {
            return;
        }

        _miniMap.IsVisible = _settings.IsMiniMapVisible;
        UpdateMiniMapPosition();
        UpdateExploration(dt);
        UpdateFlush(dt);
        HandleLargeMapHotkey();
    }

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
        TravelMapRuntimePolicy.CleanupRuntime(
            () => _lifetimeCancellation.Cancel(),
            () => _chunkLoader?.Dispose(),
            () => _dispatcher?.Dispose(),
            () =>
            {
                CleanupUi();
                _lifetimeCancellation.Dispose();
                base.OnEntityRemoved();
            });
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

    private void InitializeUi()
    {
        _uiActions = new TrackedUiActionRunner(_ => ShowMessage("地图操作未能完成"));
        var appRoot = Engine.Storage.GetSystemPath("app:/SurvivalcraftTravelMap");
        var legacySettingsPath = Engine.Storage.GetSystemPath("app:/GPSSetting.xml");
        _settingsStore = new TravelMapSettingsStore(appRoot, legacySettingsPath);
        _settings = _settingsStore.LoadAsync(_lifetimeCancellation.Token)
            .GetAwaiter()
            .GetResult();

        var gameInfo = Project.FindSubsystem<SubsystemGameInfo>(true);
        var serverHost = CommonLib.Net.Server?.Peer?.Address?.ToString()
            ?? CommonLib.Net.Server?.IPPoint?.Address.ToString();
        var serverPort = CommonLib.Net.Server?.Peer?.Port
            ?? CommonLib.Net.Server?.IPPoint?.Port;
        var identity = new TravelMapStorageIdentityInput(
            WorkType,
            appRoot,
            gameInfo.DirectoryName,
            serverHost,
            serverPort,
            gameInfo.DirectoryName,
            Player.PlayerData.PlayerGUID);
        if (!TravelMapStorageIdentity.TryResolve(identity, out var storage, out var identityError))
        {
            Engine.Log.Warning($"[TravelMap] Persistence disabled: {identityError}");
            ShowMessage("缺少可靠的世界或玩家身份，旅行地图持久化已禁用");
            return;
        }

        _tileStore = new ExplorationTileStore(Path.Combine(storage!.Directory, "tiles"));
        _waypointRepository = new WaypointRepository(storage.Directory);
        var waypointLoadOutcome = _waypointRepository.LoadAsync(_lifetimeCancellation.Token)
            .GetAwaiter()
            .GetResult();
        _waypoints = _waypointRepository.GetAll();

        TryCreateExplorationRecorder();
        var pixelSource = new TileStoreMapPixelSource(_tileStore);
        _miniMap = new MiniMapRenderer(
            pixelSource,
            _settings,
            GetPlayerPose,
            () => _waypoints,
            GetTerrainBrightness);
        Player.GuiWidget.Children.Add(_miniMap);
        UpdateMiniMapPosition();

        _largeMapDialog = new TravelMapDialog(
            pixelSource,
            _settings,
            _settingsStore,
            GetPlayerPose,
            () => _waypoints,
            GetTerrainBrightness,
            HandleContextActionAsync,
            ShowMessage);
        if (waypointLoadOutcome == WaypointLoadOutcome.CorruptIsolated)
        {
            ShowMessage("坐标点文件已损坏，已隔离并使用空列表");
        }
    }

    private void TryCreateExplorationRecorder()
    {
        if (_tileStore is null || !ModsManager.GetModEntity("SurvivalcraftTravelMap", out var modEntity))
        {
            return;
        }

        try
        {
            modEntity.GetAssetsFile("BlockPixelColor.json", stream =>
            {
                var sampler = new TerrainMapSampler(new SurvivalcraftTerrainMapSource(Terrain), stream);
                _explorationRecorder = new ExplorationRecorder(sampler, _tileStore);
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Engine.Log.Warning($"[TravelMap] Terrain map colors could not be loaded: {exception.Message}");
        }
    }

    private void UpdateMiniMapPosition()
    {
        if (_miniMap is null || _settings is null)
        {
            return;
        }

        var viewport = Player.GameWidget.ActiveCamera.ViewportSize;
        var x = MathF.Max(0f, viewport.X - _settings.MiniMapSize - 100f);
        if (Player.GuiWidget is CanvasWidget canvas)
        {
            canvas.SetWidgetPosition(_miniMap, new Engine.Vector2(x, 32f));
        }
        else
        {
            _miniMap.LayoutTransform = Engine.Matrix.CreateTranslation(x, 32f, 0f);
        }
    }

    private void UpdateExploration(float dt)
    {
        if (_explorationRecorder is null)
        {
            return;
        }

        var position = Player.ComponentBody.Position;
        var x = (int)MathF.Floor(position.X);
        var z = (int)MathF.Floor(position.Z);
        var moved = x != _lastRecordedX || z != _lastRecordedZ;
        _stationaryRecordElapsed += MathF.Max(0f, dt);
        if (!moved && _stationaryRecordElapsed < 0.5f)
        {
            return;
        }

        _lastRecordedX = x;
        _lastRecordedZ = z;
        _stationaryRecordElapsed = 0f;
        var radius = Math.Max(1, SettingsManager.VisibilityRange / 2);
        _explorationRecorder.RecordVisibleArea(x, z, radius);
    }

    private void UpdateFlush(float dt)
    {
        if (_tileStore is null)
        {
            return;
        }

        if (_flushTask is { IsCompleted: true })
        {
            if (_flushTask.IsFaulted)
            {
                Engine.Log.Warning($"[TravelMap] Map flush failed: {_flushTask.Exception?.GetBaseException().Message}");
            }

            _flushTask = null;
        }

        _flushElapsed += MathF.Max(0f, dt);
        if (_flushTask is null && _flushElapsed >= (float)_tileStore.FlushInterval.TotalSeconds)
        {
            _flushElapsed = 0f;
            _flushTask = _tileStore.FlushAsync(_lifetimeCancellation.Token);
        }
    }

    private void HandleLargeMapHotkey()
    {
        if (_largeMapDialog is null || DialogsManager.Dialogs.Contains(_largeMapDialog))
        {
            return;
        }

        var input = Player.GameWidget.Input;
        var chat = Player.GameWidget.messageWidget;
        var focus = TravelMapInputFocusEvaluator.Evaluate(new TravelMapInputFocusSignals(
            HasFocusedTextBox(Player.GuiWidget),
            chat?.IsVisible == true,
            chat?.EditText?.HasFocus == true,
            Gui?.ModalPanelWidget is not null || DialogsManager.HasDialogs(Player.GuiWidget)));
        var command = _uiController.HandleOpenHotkey(
            input.IsKeyDownOnce(Engine.Input.Key.M),
            focus);
        if (command.Kind == TravelMapUiCommandKind.OpenLargeMap)
        {
            _largeMapDialog.ResetToPlayer();
            DialogsManager.ShowDialog(Player.GuiWidget, _largeMapDialog);
            input.Clear();
        }
    }

    private async Task<TravelMapActionStatus> HandleContextActionAsync(
        TravelMapContextAction action,
        TravelMapContextMenu menu,
        CancellationToken cancellationToken)
    {
        if (_waypointRepository is null)
        {
            return TravelMapActionStatus.Failed;
        }

        switch (action)
        {
            case TravelMapContextAction.TeleportNearby:
            {
                var target = new Vector3(menu.WorldPosition.X, Player.ComponentBody.Position.Y, menu.WorldPosition.Y);
                return await RequestSurfaceTravelAsync(target, cancellationToken).ConfigureAwait(false);
            }
            case TravelMapContextAction.TeleportToWaypoint:
            {
                var waypoint = FindWaypoint(menu.WaypointId);
                return waypoint is null
                    ? TravelMapActionStatus.Failed
                    : await RequestWaypointTravelAsync(waypoint.Position, cancellationToken).ConfigureAwait(false);
            }
            case TravelMapContextAction.AddWaypoint:
            {
                var x = checked((int)MathF.Floor(menu.WorldPosition.X));
                var z = checked((int)MathF.Floor(menu.WorldPosition.Y));
                var y = Terrain.Terrain.GetTopHeight(x, z) + 1;
                _waypointRepository.Add($"坐标点 {x}, {z}", new Vector3(x + 0.5f, y, z + 0.5f));
                await SaveWaypointsAsync(cancellationToken).ConfigureAwait(false);
                return TravelMapActionStatus.Completed;
            }
            case TravelMapContextAction.RenameWaypoint:
            {
                var waypoint = FindWaypoint(menu.WaypointId);
                if (waypoint is null)
                {
                    return TravelMapActionStatus.Failed;
                }

                DialogsManager.Promit(
                    "重命名坐标点",
                    waypoint.Name,
                    name => _uiActions?.TryRun(_ => RenameWaypointAsync(waypoint.Id, name)));
                return TravelMapActionStatus.Completed;
            }
            case TravelMapContextAction.DeleteWaypoint:
                if (!menu.WaypointId.HasValue || !_waypointRepository.Remove(menu.WaypointId.Value))
                {
                    return TravelMapActionStatus.Failed;
                }

                await SaveWaypointsAsync(cancellationToken).ConfigureAwait(false);
                return TravelMapActionStatus.Completed;
            case TravelMapContextAction.Cancel:
                return TravelMapActionStatus.Cancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private async Task<TravelMapActionStatus> RequestSurfaceTravelAsync(
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var router = new TravelMapTeleportRouter(
            WorkType,
            async (position, token) =>
            {
                var result = await TeleportToSurfaceAsync((int)position.X, (int)position.Z, token).ConfigureAwait(false);
                return result == TeleportResult.Success
                    ? TravelMapTeleportDispatchResult.LocalRequested
                    : TravelMapTeleportDispatchResult.LocalFailed;
            },
            ClientTravelCommand);
        return ToActionStatus(await router.RequestAsync(target, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TravelMapActionStatus> RequestWaypointTravelAsync(
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var router = new TravelMapTeleportRouter(
            WorkType,
            async (position, token) =>
            {
                var result = await TeleportToWaypointAsync(position, token).ConfigureAwait(false);
                return result == TeleportResult.Success
                    ? TravelMapTeleportDispatchResult.LocalRequested
                    : TravelMapTeleportDispatchResult.LocalFailed;
            },
            ClientTravelCommand);
        return ToActionStatus(await router.RequestAsync(target, cancellationToken).ConfigureAwait(false));
    }

    private Waypoint? FindWaypoint(Guid? id) => id.HasValue
        ? _waypoints.FirstOrDefault(waypoint => waypoint.Id == id.Value)
        : null;

    private async Task RenameWaypointAsync(Guid id, string name)
    {
        try
        {
            if (_waypointRepository is not null && _waypointRepository.Rename(id, name))
            {
                await SaveWaypointsAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            ShowMessage("坐标点名称未保存");
        }
    }

    private async Task SaveWaypointsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _waypoints = await WaypointPersistence.SaveOrReloadAsync(
                _waypointRepository!,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _waypoints = _waypointRepository!.GetAll();
            throw;
        }
    }

    private PlayerMapPose GetPlayerPose()
    {
        var position = Player.ComponentBody.Position;
        var forward = Player.ComponentBody.Matrix.Forward;
        return new PlayerMapPose(
            new Vector3(position.X, position.Y, position.Z),
            MathF.Atan2(forward.X, -forward.Z));
    }

    private float GetTerrainBrightness() => _settings is { UseDayNightTint: true }
        ? DayNightBrightness.Calculate(TimeOfDay.TimeOfDay, _settings.NightMinimumBrightness)
        : 1f;

    private void ShowMessage(string message)
    {
        try
        {
            _dispatcher?.Invoke(() => Gui?.DisplaySmallMessage(
                message,
                Engine.Color.White,
                blinking: false,
                playNotificationSound: false));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CleanupUi()
    {
        var uiActions = _uiActions;
        uiActions?.Dispose();
        _uiActions = null;
        var shutdownClock = System.Diagnostics.Stopwatch.StartNew();
        var shutdownLimit = TimeSpan.FromSeconds(2);
        TimeSpan RemainingTime() => shutdownClock.Elapsed >= shutdownLimit
            ? TimeSpan.Zero
            : shutdownLimit - shutdownClock.Elapsed;
        static void ReportShutdownFailure(Exception exception) =>
            Engine.Log.Warning($"[TravelMap] Shutdown operation failed: {exception.Message}");
        if (_largeMapDialog is not null)
        {
            DialogsManager.HideDialog(_largeMapDialog);
            _largeMapDialog.Dispose();
            _largeMapDialog = null;
        }

        if (_miniMap is not null)
        {
            _miniMap.ParentWidget?.Children.Remove(_miniMap);
            _miniMap.Dispose();
            _miniMap = null;
        }

        if (_tileStore is not null)
        {
            var pendingFlushCompleted = _flushTask is null
                || BoundedTaskObserver.ObserveWithin(
                    _flushTask,
                    RemainingTime(),
                    ReportShutdownFailure);
            if (pendingFlushCompleted && RemainingTime() > TimeSpan.Zero)
            {
                using var flushCancellation = new CancellationTokenSource(RemainingTime());
                BoundedTaskObserver.ObserveWithin(
                    _tileStore.FlushAsync(flushCancellation.Token),
                    RemainingTime(),
                    ReportShutdownFailure);
            }
        }

        if (_settingsStore is not null
            && _settings is not null
            && RemainingTime() > TimeSpan.Zero)
        {
            using var settingsCancellation = new CancellationTokenSource(RemainingTime());
            BoundedTaskObserver.ObserveWithin(
                _settingsStore.SaveAsync(_settings, settingsCancellation.Token),
                RemainingTime(),
                ReportShutdownFailure);
        }

        if (uiActions is not null && RemainingTime() > TimeSpan.Zero)
        {
            BoundedTaskObserver.ObserveWithin(
                uiActions.WhenIdleAsync(),
                RemainingTime(),
                ReportShutdownFailure);
        }
    }

    private static bool HasFocusedTextBox(Widget widget)
    {
        if (widget is TextBoxWidget { HasFocus: true })
        {
            return true;
        }

        if (widget is ContainerWidget container)
        {
            foreach (var child in container.Children)
            {
                if (HasFocusedTextBox(child))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static TravelMapActionStatus ToActionStatus(TravelMapTeleportDispatchResult result) => result switch
    {
        TravelMapTeleportDispatchResult.LocalRequested => TravelMapActionStatus.Completed,
        TravelMapTeleportDispatchResult.CommandQueued => TravelMapActionStatus.Completed,
        TravelMapTeleportDispatchResult.LocalFailed => TravelMapActionStatus.Failed,
        TravelMapTeleportDispatchResult.Unavailable => TravelMapActionStatus.Unavailable,
        _ => TravelMapActionStatus.Failed,
    };
}
