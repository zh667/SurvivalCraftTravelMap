using System.Numerics;
using Engine.Graphics;
using Game;
using Game.NetWork;
using Game.NetWork.Packages;
using GameEntitySystem;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Network;
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

public readonly record struct TravelMapRuntimeContext(
    TravelMapWorkType WorkType,
    bool IsMainPlayer,
    bool HasUi);

public static class TravelMapRuntimePolicy
{
    public static bool CreatesUi(TravelMapRuntimeContext context) =>
        context.IsMainPlayer && context.HasUi;

    public static bool CreatesTeleportService(TravelMapWorkType workType) => workType is not TravelMapWorkType.Client;

    public static bool AllowsDirectPositionWrite(TravelMapRuntimeContext context) =>
        context.WorkType == TravelMapWorkType.Local;

    public static bool UsesAuthoritativeHostTeleport(TravelMapRuntimeContext context) =>
        context.WorkType == TravelMapWorkType.Server && CreatesUi(context);

    public static bool UsesLocalWorldStorage(TravelMapRuntimeContext context) =>
        context.WorkType == TravelMapWorkType.Local
        || (context.WorkType == TravelMapWorkType.Server && context.IsMainPlayer);

    public static bool CreatesInvitationUi(TravelMapRuntimeContext context) =>
        context.WorkType != TravelMapWorkType.Local && CreatesUi(context);

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
    private const int MaximumChunkAttemptsPerFrame = 4;
    private const int MaximumCoverageChecksPerFrame = 4;
    private static int s_nextUpdateLocationId = -1_000_000;
    private static readonly CoordinateTeleportFutureSchemaWarningGate ServerSettingsWarningGate = new();
    private static readonly TravelMapSettingsFutureSchemaWarningGate SettingsWarningGate = new();

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TerrainChunkExplorationScheduler _explorationScheduler = new();
    private readonly ExplorationFailureReporter _explorationFailureReporter = new(LogExplorationFailure);
    private readonly TravelMapUiController _uiController = new();
    private readonly IdempotentTravelMapCleanup _runtimeCleanup;
    private GameUpdateDispatcher? _dispatcher;
    private SurvivalcraftChunkLoader? _chunkLoader;
    private TravelMapSettingsStore? _settingsStore;
    private TravelMapSettings? _settings;
    private ExplorationTileStore? _tileStore;
    private ExplorationRecorder? _explorationRecorder;
    private ExplorationCoverageProbe? _explorationCoverageProbe;
    private WaypointRepository? _waypointRepository;
    private CurrentPositionWaypointHandler? _currentPositionWaypointHandler;
    private IReadOnlyList<Waypoint> _waypoints = Array.Empty<Waypoint>();
    private MiniMapRenderer? _miniMap;
    private TravelMapDialog? _largeMapDialog;
    private TrackedUiActionRunner? _uiActions;
    private Task? _flushTask;
    private readonly object _networkSync = new();
    private CoordinateTeleportServerSession? _coordinateServerSession;
    private AuthoritativeHostTeleportSession? _authoritativeHostTeleport;
    private CoordinateTeleportClientSession? _coordinateClientSession;
    private CoordinateTeleportServerOptions _coordinateServerOptions = new();
    private TrackedUiActionRunner? _networkActions;
    private TeleportPanelWidget? _teleportPanel;
    private BitmapButtonWidget? _teleportPanelButton;
    private Texture2D? _teleportButtonTexture;
    private Texture2D? _teleportButtonPressedTexture;
    private MinimapExplorationFootprintIdentity? _explorationFootprintIdentity;
    private float _flushElapsed;
    private bool _explorationPressureWarningShown;
    private bool _isActive;
    private bool _persistenceWarningShown;

    public TravelMapComponent()
    {
        _runtimeCleanup = new IdempotentTravelMapCleanup(CleanupRuntimeResources);
    }

    internal ComponentPlayer Player { get; private set; } = null!;

    internal SubsystemTerrain Terrain { get; private set; } = null!;

    internal SubsystemTimeOfDay TimeOfDay { get; private set; } = null!;

    internal ComponentGui? Gui { get; private set; }

    internal SafeTeleportService? TeleportService { get; private set; }

    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public TravelMapWorkType WorkType { get; private set; }

    public TravelMapRuntimeContext RuntimeContext { get; private set; }

    public Action<TravelMapClientTravelCommand>? ClientTravelCommand { get; set; }

    public UpdateOrder UpdateOrder => UpdateOrder.Views;

    public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        if (!TravelMapStartup.EnsureInitialized(
                packageName => ModsManager.GetModEntity(packageName, out _),
                PackageManager.RegisterPackage,
                PackageManager.UnRegisterPackage,
                message => Engine.Log.Warning($"[TravelMap] {message}")))
        {
            return;
        }

        var uiState = new UiInitializationState();
        Action[] stages =
        [
            InitializeCoreRuntime,
            () => InitializeUiSettings(uiState),
            () => InitializeUiPersistence(uiState),
            TryCreateExplorationRecorder,
            () => AttachUiWidgets(uiState),
        ];

        _isActive = TravelMapLoadTransaction.TryRun(
            stages,
            _runtimeCleanup.Run,
            exception => Engine.Log.Warning(
                $"[TravelMap] Player component activation failed; the component is inert: {exception.Message}"));
        if (_isActive)
        {
            Engine.Log.Information(
                $"[TravelMap] Player component active: workType={WorkType}, main={RuntimeContext.IsMainPlayer}, ui={_miniMap is not null}.");
        }
    }

    public void Update(float dt)
    {
        if (!_isActive)
        {
            return;
        }

        _dispatcher?.Pump();
        if (WorkType == TravelMapWorkType.Server && Project is ProjectNet projectNet)
        {
            TravelMapNetworkRuntime.UpdateLegacyServer(projectNet, CommonLib.Net);
        }

        var hudState = TravelMapHudPolicy.Evaluate(GetHudSignals());
        ApplyHudState(hudState);
        UpdateHudPositions();
        UpdateInvitationUi(hudState);
        UpdateExploration();
        HandleLargeMapHotkey();
        if (_miniMap is null || _settings is null)
        {
            return;
        }

        UpdateFlush(dt);
    }

    public async Task<TeleportResult> TeleportToSurfaceAsync(
        int x,
        int z,
        CancellationToken cancellationToken)
    {
        using var diagnosticScope = TeleportDiagnosticContext.Ensure(
            new TeleportRequestDiagnosticContext("local", null, "SurfaceRequest"));
        var service = GetLocalTeleportService();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var result = await service.TeleportToSurfaceAsync(x, z, linkedCancellation.Token);
        ShowMessage(TravelMapNoticeFactory.For(result));
        return result;
    }

    public async Task<TeleportResult> TeleportToWaypointAsync(
        Vector3 xyz,
        CancellationToken cancellationToken)
    {
        using var diagnosticScope = TeleportDiagnosticContext.Ensure(
            new TeleportRequestDiagnosticContext("local", null, "WaypointRequest"));
        var service = GetLocalTeleportService();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var result = await service.TeleportToWaypointAsync(xyz, linkedCancellation.Token);
        ShowMessage(TravelMapNoticeFactory.For(result));
        return result;
    }

    public override void OnEntityRemoved()
    {
        _runtimeCleanup.Run();
        base.OnEntityRemoved();
    }

    private SafeTeleportService GetLocalTeleportService()
    {
        if (!TravelMapRuntimePolicy.AllowsDirectPositionWrite(RuntimeContext) || TeleportService is null)
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

    internal TravelMapBoundPeer? TryBindNetworkPeer(Client source) =>
        _isActive && WorkType == TravelMapWorkType.Server
            ? TravelMapBoundPeer.TryCreate(source, Player, LifetimeToken)
            : null;

    internal async Task<CoordinateTeleportMessage> HandleCoordinateServerAsync(
        TravelMapBoundPeer binding,
        CoordinateTeleportMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(message);
        using var diagnosticScope = TeleportDiagnosticContext.Ensure(
            new TeleportRequestDiagnosticContext(
                "remote",
                message.RequestId,
                message.Kind.ToString()));
        var sourceIdentity = binding.Identity;
        CoordinateTeleportServerSession session;
        lock (_networkSync)
        {
            if (WorkType != TravelMapWorkType.Server
                || TeleportService is null
                || !binding.IsCurrent)
            {
                var rejected = CoordinateTeleportMessage.Result(
                    message.RequestId,
                    CoordinateTeleportResultCode.Rejected);
                ShowMessage(TravelMapNoticeFactory.For(CoordinateTeleportResultCode.Rejected));
                return rejected;
            }

            _coordinateServerSession ??= new CoordinateTeleportServerSession(
                sourceIdentity,
                new SafeTeleportExecutor(TeleportService),
                _coordinateServerOptions);
            session = _coordinateServerSession;
        }

        var response = await CoordinateTeleportBoundOperation.ExecuteAsync(
            binding,
            session,
            message,
            cancellationToken).ConfigureAwait(false);
        ReportCoordinateTeleportResult("remote", message, response.ResultCode);
        if (response.ResultCode is { } result)
        {
            ShowMessage(TravelMapNoticeFactory.For(result));
        }

        return response;
    }

    internal bool ReceiveCoordinateServerMessage(Client source, CoordinateTeleportMessage message)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_networkSync)
        {
            return WorkType == TravelMapWorkType.Client
                && _coordinateClientSession is not null
                && _coordinateClientSession.Receive(
                    TravelMapNetworkPeerIdentity.ForClient(source),
                    message);
        }
    }

    internal async Task<TeleportResult> HandleLegacyTeleportToPlayerAsync(
        Vector3 target,
        CancellationToken cancellationToken)
    {
        using var diagnosticScope = TeleportDiagnosticContext.Ensure(
            new TeleportRequestDiagnosticContext("invitation", null, "Teleport"));
        if (WorkType != TravelMapWorkType.Server || TeleportService is null)
        {
            return TeleportResult.OutOfWorld;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var result = await TeleportService.TeleportToWaypointAsync(target, linked.Token).ConfigureAwait(false);
        Engine.Log.Information($"[TravelMap] Legacy invitation teleport result={result}.");
        return result;
    }

    internal void HandleLegacyClientResponse(LegacyGpsMessage message)
    {
        if (_settings is null)
        {
            return;
        }

        var action = LegacyInvitationClientPolicy.Decide(
            message,
            _settings.AcceptTeleportInvitations);
        switch (action)
        {
            case LegacyClientResponseAction.ShowMessage:
                ShowMessage(message.Message ?? string.Empty);
                break;
            case LegacyClientResponseAction.RejectInvitation:
                QueueLegacyPackage(LegacyGpsMessage.TeleportAllow(false));
                ShowMessage("已按设置自动拒绝玩家传送邀请");
                break;
            case LegacyClientResponseAction.ShowInvitation:
                try
                {
                    _dispatcher?.Invoke(() => DialogsManager.ShowDialog(
                        Player.GuiWidget,
                        new MessageDialog(
                            "玩家传送邀请",
                            message.Message ?? string.Empty,
                            LanguageControl.Ok,
                            LanguageControl.Cancel,
                            button => QueueLegacyPackage(
                                LegacyGpsMessage.TeleportAllow(button == MessageDialogButton.Button1)))));
                }
                catch (ObjectDisposedException)
                {
                }

                break;
        }
    }

    private void InitializeCoordinateClient()
    {
        var server = CommonLib.Net.Server;
        if (server is null)
        {
            ShowMessage(CoordinateTeleportClientSession.UnsupportedOrTimeoutMessage);
            return;
        }

        _networkActions = new TrackedUiActionRunner(
            _ => ShowMessage("联机旅行请求未能完成", TravelMapNoticeKind.Failure));
        _coordinateClientSession = new CoordinateTeleportClientSession(
            TravelMapNetworkPeerIdentity.ForClient(server),
            message =>
            {
                var package = new CoordinateTeleportPackage(message) { To = server };
                CommonLib.Net.QueuePackage(package);
            },
            new SystemCoordinateTeleportProtocolClock(),
            message => ShowMessage(message));
        ClientTravelCommand = QueueCoordinateClientCommand;
    }

    private void QueueCoordinateClientCommand(TravelMapClientTravelCommand command)
    {
        var runner = _networkActions;
        if (runner is null || !runner.TryRun(token => SendCoordinateClientCommandAsync(command, token)))
        {
            ShowMessage("另一项联机旅行请求仍在进行", TravelMapNoticeKind.Information);
        }
    }

    private async Task SendCoordinateClientCommandAsync(
        TravelMapClientTravelCommand command,
        CancellationToken cancellationToken)
    {
        CoordinateTeleportClientSession? session;
        lock (_networkSync)
        {
            session = _coordinateClientSession;
        }

        if (session is null)
        {
            ShowMessage(CoordinateTeleportClientSession.UnsupportedOrTimeoutMessage);
            return;
        }

        var result = command.Mode == TravelMapClientTravelMode.Surface
            ? await session.RequestSurfaceAsync(
                checked((int)MathF.Floor(command.Target.X)),
                checked((int)MathF.Floor(command.Target.Z)),
                cancellationToken).ConfigureAwait(false)
            : await session.RequestWaypointAsync(command.Target, cancellationToken).ConfigureAwait(false);
        ShowMessage(TravelMapNoticeFactory.For(result));
    }

    private void InitializeCoreRuntime()
    {
        var player = Entity.FindComponent<ComponentPlayer>(true)
            ?? throw new InvalidOperationException("TravelMapComponent must be attached to a player entity.");
        var playerBody = player.ComponentBody
            ?? throw new InvalidOperationException("The travel-map player does not have a body component.");
        Player = player;
        Terrain = Project.FindSubsystem<SubsystemTerrain>(true);
        TimeOfDay = Project.FindSubsystem<SubsystemTimeOfDay>(true);
        WorkType = ToTravelMapWorkType(CommonLib.WorkType);
        RuntimeContext = new TravelMapRuntimeContext(
            WorkType,
            player.PlayerData.IsMainPlayer,
            player.ComponentGui is not null);
        if (WorkType == TravelMapWorkType.Server)
        {
            try
            {
                var optionsPath = Engine.Storage.GetSystemPath(
                    "app:/SurvivalcraftTravelMap/server-settings.json");
                var loadResult = new CoordinateTeleportServerOptionsStore(optionsPath).LoadWithOutcome();
                _coordinateServerOptions = loadResult.Options;
                ServerSettingsWarningGate.NotifyIfNeeded(loadResult, static message => Engine.Log.Warning(message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Engine.Log.Warning($"[TravelMap] Server teleport settings could not be loaded: {exception.Message}");
                _coordinateServerOptions = new CoordinateTeleportServerOptions();
            }
        }

        Gui = TravelMapRuntimePolicy.CreatesUi(RuntimeContext)
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
            WorkType == TravelMapWorkType.Server
                ? new GameUpdateTeleportPositionCommitter(
                    _dispatcher,
                    QueueAuthoritativePositionSync)
                : new GameUpdateTeleportPositionCommitter(
                    _dispatcher,
                    static () => { }),
            clock,
            TeleportDiagnosticReporter.Report,
            TeleportDiagnosticReporter.ReportSearch);
        if (TravelMapRuntimePolicy.UsesAuthoritativeHostTeleport(RuntimeContext))
        {
            _authoritativeHostTeleport = new AuthoritativeHostTeleportSession(
                $"host:{Player.PlayerGuid:N}",
                new SafeTeleportExecutor(TeleportService),
                _coordinateServerOptions,
                (message, result) => ReportCoordinateTeleportResult("host", message, result));
        }
    }

    private void QueueAuthoritativePositionSync() =>
        CommonLib.Net.QueuePackage(
            new ComponentPlayerPackage(
                Player,
                ComponentPlayerPackage.PlayerAction.PositionSet));

    private static void ReportCoordinateTeleportResult(
        string route,
        CoordinateTeleportMessage request,
        CoordinateTeleportResultCode? result)
    {
        Engine.Log.Information(
            $"[TravelMap] Coordinate teleport route={route}, request={request.RequestId}, kind={request.Kind}, result={result?.ToString() ?? "Missing"}.");
    }

    private void InitializeUiSettings(UiInitializationState state)
    {
        if (!TravelMapRuntimePolicy.CreatesUi(RuntimeContext))
        {
            return;
        }

        _uiActions = new TrackedUiActionRunner(
            _ => ShowMessage("地图操作未能完成", TravelMapNoticeKind.Failure));
        state.AppRoot = Engine.Storage.GetSystemPath("app:/SurvivalcraftTravelMap");
        var legacySettingsPath = Engine.Storage.GetSystemPath("app:/GPSSetting.xml");
        _settingsStore = new TravelMapSettingsStore(state.AppRoot, legacySettingsPath);
        try
        {
            var loadResult = _settingsStore.LoadWithOutcomeAsync(_lifetimeCancellation.Token)
                .GetAwaiter()
                .GetResult();
            _settings = loadResult.Settings;
            SettingsWarningGate.NotifyIfNeeded(loadResult, message => ShowMessage(message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings = new TravelMapSettings();
            _settingsStore.EnterReadOnlyMode();
            WarnPersistenceOnce($"地图设置不可写，本次使用内存默认值：{exception.Message}");
        }
    }

    private void InitializeUiPersistence(UiInitializationState state)
    {
        if (!TravelMapRuntimePolicy.CreatesUi(RuntimeContext) || _settings is null)
        {
            return;
        }

        var gameInfo = Project.FindSubsystem<SubsystemGameInfo>(true);
        var serverHost = CommonLib.Net.Server?.Peer?.Address?.ToString()
            ?? CommonLib.Net.Server?.IPPoint?.Address.ToString();
        var serverPort = CommonLib.Net.Server?.Peer?.Port
            ?? CommonLib.Net.Server?.IPPoint?.Port;
        var storageScope = TravelMapRuntimePolicy.UsesLocalWorldStorage(RuntimeContext)
            ? TravelMapStorageScope.LocalWorld
            : TravelMapStorageScope.RemoteServer;
        var identity = new TravelMapStorageIdentityInput(
            storageScope,
            state.AppRoot,
            gameInfo.DirectoryName,
            serverHost,
            serverPort,
            gameInfo.DirectoryName,
            Player.PlayerData.PlayerGUID);
        if (!TravelMapStorageIdentity.TryResolve(identity, out var storage, out var identityError))
        {
            Engine.Log.Warning($"[TravelMap] Persistence disabled: {identityError}");
            WarnPersistenceOnce("缺少可靠的世界或玩家身份，旅行地图持久化已禁用");
            throw new InvalidOperationException(
                $"Travel-map persistence identity is unavailable: {identityError}");
        }

        try
        {
            _tileStore = new ExplorationTileStore(Path.Combine(storage!.Directory, "tiles"));
            _waypointRepository = new WaypointRepository(storage.Directory);
            state.WaypointLoadOutcome = _waypointRepository.LoadAsync(_lifetimeCancellation.Token)
                .GetAwaiter()
                .GetResult();
            _waypoints = _waypointRepository.GetAll();
            _currentPositionWaypointHandler = new CurrentPositionWaypointHandler(
                _waypointRepository,
                () =>
                {
                    var position = Player.ComponentBody.Position;
                    return new Vector3(position.X, position.Y, position.Z);
                });
            state.PixelSource = new TileStoreMapPixelSource(_tileStore);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _tileStore = null;
            _waypointRepository = null;
            _currentPositionWaypointHandler = null;
            WarnPersistenceOnce($"地图与坐标点持久化不可用：{exception.Message}");
            throw;
        }
    }

    private void AttachUiWidgets(UiInitializationState state)
    {
        if (!TravelMapRuntimePolicy.CreatesUi(RuntimeContext)
            || _settings is null
            || _settingsStore is null)
        {
            return;
        }

        if (TravelMapRuntimePolicy.CreatesInvitationUi(RuntimeContext))
        {
            InitializeInvitationUi();
        }

        if (state.PixelSource is null)
        {
            if (WorkType == TravelMapWorkType.Client)
            {
                InitializeCoordinateClient();
            }

            return;
        }

        _miniMap = new MiniMapRenderer(
            state.PixelSource,
            _settings,
            _settingsStore,
            GetPlayerPose,
            () => _waypoints,
            GetTerrainBrightness,
            IsMapInputBlocked,
            OpenLargeMap,
            message => ShowMessage(message));
        Player.GuiWidget.Children.Add(_miniMap);
        UpdateHudPositions();

        _largeMapDialog = new TravelMapDialog(
            state.PixelSource,
            _settings,
            _settingsStore,
            GetPlayerPose,
            () => _waypoints,
            GetTerrainBrightness,
            HandleContextActionAsync,
            ShowMessage);
        if (state.WaypointLoadOutcome == WaypointLoadOutcome.CorruptIsolated)
        {
            ShowMessage("坐标点文件已损坏，已隔离并使用空列表", TravelMapNoticeKind.Failure);
        }

        if (WorkType == TravelMapWorkType.Client)
        {
            InitializeCoordinateClient();
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
                _explorationCoverageProbe = new ExplorationCoverageProbe(
                    _explorationRecorder.IsChunkFullyExplored,
                    _explorationFailureReporter);
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Engine.Log.Warning($"[TravelMap] Terrain map colors could not be loaded: {exception.Message}");
        }
    }

    private void UpdateHudPositions()
    {
        if (_settings is null)
        {
            return;
        }

        var guiSize = Player.GuiWidget.ActualSize;
        var positions = TravelMapOverlayLayout.PlaceHud(
            new Vector2(guiSize.X, guiSize.Y),
            _settings.MiniMapSize);
        if (_miniMap is not null)
        {
            SetHudWidgetPosition(_miniMap, positions.MiniMap);
        }

        if (_teleportPanelButton is not null)
        {
            SetHudWidgetPosition(_teleportPanelButton, positions.TeleportButton);
        }
    }

    private void SetHudWidgetPosition(Widget widget, Vector2 position)
    {
        if (Player.GuiWidget is CanvasWidget canvas)
        {
            canvas.SetWidgetPosition(widget, new Engine.Vector2(position.X, position.Y));
        }
        else
        {
            widget.LayoutTransform = Engine.Matrix.CreateTranslation(position.X, position.Y, 0f);
        }
    }

    private void InitializeInvitationUi()
    {
        _teleportPanel = new TeleportPanelWidget(GetLegacyTeleportPlayers, targetId =>
            QueueLegacyPackage(LegacyGpsMessage.Teleport(targetId.ToString())));
        if (!ModsManager.GetModEntity("SurvivalcraftTravelMap", out var modEntity))
        {
            throw new InvalidOperationException("Travel map assets are unavailable.");
        }

        modEntity.GetAssetsFile("TeleportButton.png", stream =>
            _teleportButtonTexture = Texture2D.Load(stream));
        modEntity.GetAssetsFile("TeleportButton_Pressed.png", stream =>
            _teleportButtonPressedTexture = Texture2D.Load(stream));
        if (_teleportButtonTexture is null || _teleportButtonPressedTexture is null)
        {
            throw new InvalidDataException("Travel map invitation button textures could not be loaded.");
        }

        _teleportPanelButton = new BitmapButtonWidget
        {
            Size = new Engine.Vector2(48f, 46f),
            NormalSubtexture = new Subtexture(
                _teleportButtonTexture,
                Engine.Vector2.Zero,
                Engine.Vector2.One),
            ClickedSubtexture = new Subtexture(
                _teleportButtonPressedTexture,
                Engine.Vector2.Zero,
                Engine.Vector2.One),
        };
        Player.GuiWidget.Children.Add(_teleportPanelButton);
        UpdateHudPositions();
    }

    private void UpdateInvitationUi(TravelMapHudState hudState)
    {
        if (_teleportPanelButton is null || _teleportPanel is null)
        {
            return;
        }

        if (!hudState.ShowTeleportButton
            || !_teleportPanelButton.IsEnabled
            || !GetMapInputFocus().AllowsMapHotkey)
        {
            return;
        }

        if (_teleportPanelButton.IsClicked && !DialogsManager.Dialogs.Contains(_teleportPanel))
        {
            _teleportPanel.Refresh();
            DialogsManager.ShowDialog(Player.GuiWidget, _teleportPanel);
        }
    }

    private TravelMapHudSignals GetHudSignals()
    {
        var isLargeMapOpen = _largeMapDialog is not null
            && DialogsManager.Dialogs.Contains(_largeMapDialog);
        var hasModalSurface = Gui?.ModalPanelWidget is not null
            || DialogsManager.Dialogs.Any(dialog => !ReferenceEquals(dialog, _largeMapDialog));
        return new TravelMapHudSignals(
            HasUi: RuntimeContext.HasUi,
            IsMainPlayer: RuntimeContext.IsMainPlayer,
            IsRuntimeActive: _isActive,
            MiniMapSettingEnabled: _settings?.IsMiniMapVisible == true,
            HasModalSurface: hasModalSurface,
            IsLargeMapOpen: isLargeMapOpen,
            HasOtherPlayers: CountOtherPlayers() > 0,
            InvitationFeatureAvailable: TravelMapRuntimePolicy.CreatesInvitationUi(RuntimeContext),
            HasTextEntryFocus: !GetMapInputFocus().AllowsMapHotkey);
    }

    private void ApplyHudState(TravelMapHudState state)
    {
        if (_miniMap is not null)
        {
            _miniMap.IsVisible = state.ShowMiniMap;
            _miniMap.IsEnabled = state.AllowMiniMapInput;
        }

        if (_teleportPanelButton is not null)
        {
            _teleportPanelButton.IsVisible = state.ShowTeleportButton;
            _teleportPanelButton.IsEnabled = state.ShowTeleportButton && state.AllowMiniMapInput;
        }
    }

    private int CountOtherPlayers() => Project
        .FindSubsystem<SubsystemPlayers>(true)
        .ComponentPlayers
        .Count(player => player.PlayerGuid != Player.PlayerGuid);

    private IReadOnlyList<LegacyTeleportPlayer> GetLegacyTeleportPlayers()
    {
        var players = Project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers;
        return players
            .Where(player => player.PlayerGuid != Player.PlayerGuid)
            .Select(player => new LegacyTeleportPlayer(player.PlayerGuid, player.PlayerData.Name))
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void QueueLegacyPackage(LegacyGpsMessage message)
    {
        if (TravelMapRuntimePolicy.UsesAuthoritativeHostTeleport(RuntimeContext)
            && Project is ProjectNet projectNet)
        {
            TravelMapNetworkRuntime.HandleLegacyHost(message, Player, projectNet, CommonLib.Net);
            return;
        }

        CommonLib.Net.QueuePackage(new LegacyGpsPackage(message));
    }

    private void UpdateExploration()
    {
        if (_explorationRecorder is null || _explorationCoverageProbe is null || _settings is null)
        {
            return;
        }

        var position = Player.ComponentBody.Position;
        var footprintIdentity = MinimapExplorationFootprintIdentity.Create(
            position.X,
            position.Z,
            _settings.MiniMapSize,
            _settings.MiniMapBlocksPerPixel);
        if (_explorationFootprintIdentity != footprintIdentity)
        {
            _explorationFootprintIdentity = footprintIdentity;
            var footprint = MinimapExplorationFootprint.Create(footprintIdentity);
            _explorationScheduler.ObserveFootprint(footprint);
        }

        _explorationScheduler.ReconcileCoverage(
            _explorationCoverageProbe.IsFullyExplored,
            MaximumCoverageChecksPerFrame);

        foreach (var chunk in _explorationScheduler.GetPendingAttempts(MaximumChunkAttemptsPerFrame))
        {
            try
            {
                var result = _explorationRecorder.RecordChunk(chunk);
                if (result == ExplorationRecordResult.Recorded)
                {
                    _explorationScheduler.MarkCompleted(chunk);
                }
                else if (result == ExplorationRecordResult.Pressure && !_explorationPressureWarningShown)
                {
                    _explorationPressureWarningShown = true;
                    ShowMessage(
                        "地图存储持续失败；已暂停记录新区块，现有探索仍会保留并重试保存",
                        TravelMapNoticeKind.Failure);
                }
            }
            catch (Exception exception)
            {
                _explorationFailureReporter.Report(
                    chunk,
                    ExplorationFailureOperation.Record,
                    exception);
            }
        }
    }

    private static void LogExplorationFailure(string message)
    {
        Engine.Log.Warning(message);
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
        if (_largeMapDialog is null)
        {
            return;
        }

        var input = Player.GameWidget.Input;
        var isOpen = DialogsManager.Dialogs.Contains(_largeMapDialog);
        var focus = GetMapInputFocus(ignoreLargeMapDialog: isOpen);
        var command = _uiController.HandleToggleHotkey(
            input.IsKeyDownOnce(Engine.Input.Key.M),
            isOpen,
            focus);
        if (command.Kind == TravelMapUiCommandKind.OpenLargeMap)
        {
            OpenLargeMap();
        }
        else if (command.Kind == TravelMapUiCommandKind.CloseLargeMap)
        {
            DialogsManager.HideDialog(_largeMapDialog);
            input.Clear();
        }
    }

    private void OpenLargeMap()
    {
        if (_largeMapDialog is null || !GetMapInputFocus().AllowsMapHotkey)
        {
            return;
        }

        _largeMapDialog.ResetToPlayer();
        DialogsManager.ShowDialog(Player.GuiWidget, _largeMapDialog);
        ApplyHudState(TravelMapHudPolicy.Evaluate(GetHudSignals()));
        Player.GameWidget.Input.Clear();
    }

    private TravelMapFocusState GetMapInputFocus(bool ignoreLargeMapDialog = false)
    {
        var chat = Player.GameWidget.messageWidget;
        var hasDialogs = ignoreLargeMapDialog && _largeMapDialog is not null
            ? DialogsManager.Dialogs.Any(dialog => !ReferenceEquals(dialog, _largeMapDialog))
            : DialogsManager.HasDialogs(Player.GuiWidget);
        return TravelMapInputFocusEvaluator.Evaluate(new TravelMapInputFocusSignals(
            HasFocusedTextBox(Player.GuiWidget),
            chat?.IsVisible == true,
            chat?.EditText?.HasFocus == true,
            Gui?.ModalPanelWidget is not null || hasDialogs));
    }

    private bool IsMapInputBlocked() => !GetMapInputFocus().AllowsMapHotkey;

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
                if (_currentPositionWaypointHandler is null)
                {
                    return TravelMapActionStatus.Failed;
                }

                _waypoints = await _currentPositionWaypointHandler.SaveAsync(cancellationToken)
                    .ConfigureAwait(false);
                ShowMessage("坐标点已保存", TravelMapNoticeKind.Success);
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
                ShowMessage("坐标点已删除", TravelMapNoticeKind.Success);
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
            RuntimeContext,
            async (position, token) =>
            {
                var result = await TeleportToSurfaceAsync(
                    checked((int)MathF.Floor(position.X)),
                    checked((int)MathF.Floor(position.Z)),
                    token).ConfigureAwait(false);
                return result == TeleportResult.Success
                    ? TravelMapTeleportDispatchResult.LocalRequested
                    : TravelMapTeleportDispatchResult.LocalFailed;
            },
            RequestAuthoritativeHostTravelAsync,
            ClientTravelCommand);
        return ToActionStatus(await router.RequestSurfaceAsync(target, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TravelMapActionStatus> RequestWaypointTravelAsync(
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var router = new TravelMapTeleportRouter(
            RuntimeContext,
            async (position, token) =>
            {
                var result = await TeleportToWaypointAsync(position, token).ConfigureAwait(false);
                return result == TeleportResult.Success
                    ? TravelMapTeleportDispatchResult.LocalRequested
                    : TravelMapTeleportDispatchResult.LocalFailed;
            },
            RequestAuthoritativeHostTravelAsync,
            ClientTravelCommand);
        return ToActionStatus(await router.RequestWaypointAsync(target, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TravelMapTeleportDispatchResult> RequestAuthoritativeHostTravelAsync(
        TravelMapClientTravelCommand command,
        CancellationToken cancellationToken)
    {
        var host = _authoritativeHostTeleport;
        if (!TravelMapRuntimePolicy.UsesAuthoritativeHostTeleport(RuntimeContext) || host is null)
        {
            return TravelMapTeleportDispatchResult.Unavailable;
        }

        var result = command.Mode == TravelMapClientTravelMode.Surface
            ? await host.RequestSurfaceAsync(
                checked((int)MathF.Floor(command.Target.X)),
                checked((int)MathF.Floor(command.Target.Z)),
                cancellationToken).ConfigureAwait(false)
            : await host.RequestWaypointAsync(command.Target, cancellationToken).ConfigureAwait(false);
        ShowMessage(TravelMapNoticeFactory.For(result));
        return result == CoordinateTeleportResultCode.Success
            ? TravelMapTeleportDispatchResult.LocalRequested
            : TravelMapTeleportDispatchResult.LocalFailed;
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
                ShowMessage("坐标点已重命名", TravelMapNoticeKind.Success);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            ShowMessage("坐标点名称未保存", TravelMapNoticeKind.Failure);
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

    private void ShowMessage(TravelMapNotice notice)
    {
        ShowMessage(notice.Text, notice.Kind);
    }

    private void ShowMessage(
        string message,
        TravelMapNoticeKind kind = TravelMapNoticeKind.Information)
    {
        try
        {
            _dispatcher?.Invoke(() =>
            {
                if (_largeMapDialog is not null
                    && DialogsManager.Dialogs.Contains(_largeMapDialog))
                {
                    _largeMapDialog.ShowNotice(new TravelMapNotice(message, kind));
                    return;
                }

                Gui?.DisplaySmallMessage(
                    message,
                    Engine.Color.White,
                    blinking: false,
                    playNotificationSound: false);
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WarnPersistenceOnce(string message)
    {
        if (_persistenceWarningShown)
        {
            return;
        }

        _persistenceWarningShown = true;
        Engine.Log.Warning($"[TravelMap] {message}");
        ShowMessage(message, TravelMapNoticeKind.Failure);
    }

    private void CleanupRuntimeResources()
    {
        _isActive = false;
        _explorationScheduler.Clear();
        _explorationFootprintIdentity = null;
        _explorationFailureReporter.Clear();
        RunCleanupStep(() => _lifetimeCancellation.Cancel());
        RunCleanupStep(() =>
        {
            lock (_networkSync)
            {
                _coordinateServerSession?.Dispose();
                _coordinateServerSession = null;
                _authoritativeHostTeleport?.Dispose();
                _authoritativeHostTeleport = null;
                _coordinateClientSession?.Dispose();
                _coordinateClientSession = null;
            }
        });
        RunCleanupStep(() => _chunkLoader?.Dispose());
        RunCleanupStep(CleanupUi);
        RunCleanupStep(() => _dispatcher?.Dispose());
        _chunkLoader = null;
        _dispatcher = null;
        TeleportService = null;
        ClientTravelCommand = null;
        _settingsStore = null;
        _settings = null;
        _tileStore = null;
        _explorationRecorder = null;
        _explorationCoverageProbe = null;
        _waypointRepository = null;
        _currentPositionWaypointHandler = null;
        _waypoints = Array.Empty<Waypoint>();
        _flushTask = null;
        RunCleanupStep(() => _lifetimeCancellation.Dispose());
    }

    private static void RunCleanupStep(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[TravelMap] Cleanup step failed: {exception.Message}");
        }
    }

    private void CleanupUi()
    {
        var networkActions = _networkActions;
        _networkActions = null;
        var uiActions = _uiActions;
        _uiActions = null;
        RunCleanupStep(() => networkActions?.Dispose());
        RunCleanupStep(() => uiActions?.Dispose());
        var shutdownClock = System.Diagnostics.Stopwatch.StartNew();
        var shutdownLimit = TimeSpan.FromSeconds(2);
        TimeSpan RemainingTime() => shutdownClock.Elapsed >= shutdownLimit
            ? TimeSpan.Zero
            : shutdownLimit - shutdownClock.Elapsed;
        static void ReportShutdownFailure(Exception exception) =>
            Engine.Log.Warning($"[TravelMap] Shutdown operation failed: {exception.Message}");
        Task? dialogWork = null;
        Task? miniMapWork = null;
        if (_largeMapDialog is not null)
        {
            var largeMapDialog = _largeMapDialog;
            _largeMapDialog = null;
            RunCleanupStep(() => dialogWork = largeMapDialog.WhenBackgroundWorkIdleAsync());
            RunCleanupStep(() => DialogsManager.HideDialog(largeMapDialog));
            RunCleanupStep(largeMapDialog.Dispose);
        }

        if (_miniMap is not null)
        {
            var miniMap = _miniMap;
            _miniMap = null;
            RunCleanupStep(() => miniMapWork = miniMap.WhenSaveIdleAsync());
            RunCleanupStep(() => miniMap.ParentWidget?.Children.Remove(miniMap));
            RunCleanupStep(miniMap.Dispose);
        }

        if (_teleportPanel is not null)
        {
            var teleportPanel = _teleportPanel;
            _teleportPanel = null;
            RunCleanupStep(() => DialogsManager.HideDialog(teleportPanel));
            RunCleanupStep(teleportPanel.Dispose);
        }

        if (_teleportPanelButton is not null)
        {
            var teleportPanelButton = _teleportPanelButton;
            _teleportPanelButton = null;
            RunCleanupStep(() => teleportPanelButton.ParentWidget?.Children.Remove(teleportPanelButton));
            RunCleanupStep(teleportPanelButton.Dispose);
        }

        if (_teleportButtonTexture is not null)
        {
            var teleportButtonTexture = _teleportButtonTexture;
            _teleportButtonTexture = null;
            RunCleanupStep(teleportButtonTexture.Dispose);
        }

        if (_teleportButtonPressedTexture is not null)
        {
            var teleportButtonPressedTexture = _teleportButtonPressedTexture;
            _teleportButtonPressedTexture = null;
            RunCleanupStep(teleportButtonPressedTexture.Dispose);
        }

        if (_tileStore is not null)
        {
            var pendingFlushCompleted = true;
            RunCleanupStep(() => pendingFlushCompleted = _flushTask is null
                || BoundedTaskObserver.ObserveWithin(
                    _flushTask,
                    RemainingTime(),
                    ReportShutdownFailure));
            if (pendingFlushCompleted && RemainingTime() > TimeSpan.Zero)
            {
                RunCleanupStep(() =>
                {
                    using var flushCancellation = new CancellationTokenSource(RemainingTime());
                    BoundedTaskObserver.ObserveWithin(
                        _tileStore.FlushAsync(flushCancellation.Token),
                        RemainingTime(),
                        ReportShutdownFailure);
                });
            }
        }

        if (dialogWork is not null && RemainingTime() > TimeSpan.Zero)
        {
            RunCleanupStep(() => BoundedTaskObserver.ObserveWithin(
                    dialogWork,
                    RemainingTime(),
                    ReportShutdownFailure));
        }


        if (miniMapWork is not null && RemainingTime() > TimeSpan.Zero)
        {
            RunCleanupStep(() => BoundedTaskObserver.ObserveWithin(
                    miniMapWork,
                    RemainingTime(),
                    ReportShutdownFailure));
        }

        if (_settingsStore is not null
            && _settings is not null
            && RemainingTime() > TimeSpan.Zero)
        {
            RunCleanupStep(() =>
            {
                using var settingsCancellation = new CancellationTokenSource(RemainingTime());
                BoundedTaskObserver.ObserveWithin(
                    _settingsStore.SaveAsync(_settings, settingsCancellation.Token),
                    RemainingTime(),
                    ReportShutdownFailure);
            });
        }

        if (uiActions is not null && RemainingTime() > TimeSpan.Zero)
        {
            RunCleanupStep(() => BoundedTaskObserver.ObserveWithin(
                    uiActions.WhenIdleAsync(),
                    RemainingTime(),
                    ReportShutdownFailure));
        }

        if (networkActions is not null && RemainingTime() > TimeSpan.Zero)
        {
            RunCleanupStep(() => BoundedTaskObserver.ObserveWithin(
                    networkActions.WhenIdleAsync(),
                    RemainingTime(),
                    ReportShutdownFailure));
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
        TravelMapTeleportDispatchResult.LocalFailed => TravelMapActionStatus.FailedWithFeedback,
        TravelMapTeleportDispatchResult.Unavailable => TravelMapActionStatus.Unavailable,
        _ => TravelMapActionStatus.Failed,
    };

    private sealed class UiInitializationState
    {
        internal string AppRoot { get; set; } = string.Empty;

        internal IExploredMapPixelSource? PixelSource { get; set; }

        internal WaypointLoadOutcome WaypointLoadOutcome { get; set; }
    }
}
