using System.Numerics;
using Engine.Graphics;
using Engine.Input;
using Game;
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
    private const int MaximumChunkAttemptsPerFrame = 16;
    private const int MaximumCaveChunkAttemptsPerFrame = 2;
    private const int MaximumCoverageChecksPerFrame = 16;
    private static int s_nextUpdateLocationId = -1_000_000;
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
    private CaveExplorationStore? _caveStore;
    private ExplorationRecorder? _explorationRecorder;
    private CaveMapSampler? _caveSampler;
    private ExplorationCoverageProbe? _explorationCoverageProbe;
    private MapViewState? _mapViewState;
    private WaypointRepository? _waypointRepository;
    private DismissedDeathStore? _dismissedDeathStore;
    private readonly HashSet<DeathMarkerIdentity> _dismissedDeaths = new();
    private CurrentPositionWaypointHandler? _currentPositionWaypointHandler;
    private IReadOnlyList<Waypoint> _waypoints = Array.Empty<Waypoint>();
    private MiniMapRenderer? _miniMap;
    private TravelMapDialog? _largeMapDialog;
    private MiniMapPlacementWidget? _miniMapPlacementWidget;
    private MiniMapPlacementSession? _miniMapPlacementSession;
    private Task? _miniMapPlacementSaveTask;
    private TrackedUiActionRunner? _uiActions;
    private Task? _flushTask;
    private BevelledButtonWidget? _openMapButton;
    private float _flushElapsed;
    private bool _explorationPressureWarningShown;
    private bool _isActive;
    private bool _activationPending;
    private bool _persistenceWarningShown;

    public TravelMapComponent()
    {
        _runtimeCleanup = new IdempotentTravelMapCleanup(CleanupRuntimeResources);
    }

    internal ComponentPlayer Player { get; private set; } = null!;

    internal SubsystemTerrain Terrain { get; private set; } = null!;

    internal SubsystemTimeOfDay TimeOfDay { get; private set; } = null!;

    internal SubsystemCreatureSpawn? CreatureSpawn { get; private set; }

    internal ComponentGui? Gui { get; private set; }

    internal SafeTeleportService? TeleportService { get; private set; }

    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public TravelMapWorkType WorkType { get; private set; }

    public TravelMapRuntimeContext RuntimeContext { get; private set; }

    public UpdateOrder UpdateOrder => UpdateOrder.Views;

    public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        if (!TravelMapStartup.EnsureInitialized(
                packageName => ModsManager.GetModEntity(packageName, out _),
                message => Engine.Log.Warning($"[TravelMap] {message}")))
        {
            return;
        }

        // Defer activation to the first Update: during component Load the ComponentPlayer has not
        // been assigned its PlayerIndex/GameWidget/ComponentGui yet, so identifying the main player
        // (and attaching the HUD to its GuiWidget) here would fail silently and skip the whole map.
        _activationPending = true;
    }

    private bool IsPlayerReadyForActivation()
    {
        var player = Entity.FindComponent<ComponentPlayer>(false);
        if (player is null || player.PlayerData is null || player.ComponentGui is null)
        {
            return false;
        }

        // Readiness is confirmed by the player's GameWidget existing (below), NOT by PlayerIndex:
        // on some runtimes (e.g. the Android plugin build) PlayerData.PlayerIndex stays 0 forever
        // even after the player is fully built, which would otherwise wedge activation permanently.
        //
        // PlayerData.GameWidget throws until SubsystemGameWidgets has built this player's widget,
        // so probe the subsystem directly instead of touching the throwing property.
        var gameWidgets = Project.FindSubsystem<SubsystemGameWidgets>(false);
        if (gameWidgets is null)
        {
            return false;
        }

        foreach (var widget in gameWidgets.GameWidgets)
        {
            if (widget.PlayerData == player.PlayerData)
            {
                return true;
            }
        }

        return false;
    }

    private void Activate()
    {
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
                $"[TravelMap] Player component active (deferred): workType={WorkType}, playerIndex={Player.PlayerData.PlayerIndex}, main={RuntimeContext.IsMainPlayer}, hasUi={RuntimeContext.HasUi}, ui={_miniMap is not null}.");
        }
    }

    public void Update(float dt)
    {
        if (_activationPending)
        {
            if (!IsPlayerReadyForActivation())
            {
                return;
            }

            _activationPending = false;
            Activate();
        }

        if (!_isActive)
        {
            return;
        }

        _dispatcher?.Pump();
        var hudState = TravelMapHudPolicy.Evaluate(GetHudSignals());
        ApplyHudState(hudState);
        UpdateHudPositions();
        UpdateMiniMapPlacement();
        UpdateOpenMapButton();
        UpdateExploration();
        if (_miniMapPlacementSession is null)
        {
            HandleLargeMapHotkey();
        }
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

    private void InitializeCoreRuntime()
    {
        var player = Entity.FindComponent<ComponentPlayer>(true)
            ?? throw new InvalidOperationException("TravelMapComponent must be attached to a player entity.");
        var playerBody = player.ComponentBody
            ?? throw new InvalidOperationException("The travel-map player does not have a body component.");
        Player = player;
        Terrain = Project.FindSubsystem<SubsystemTerrain>(true);
        TimeOfDay = Project.FindSubsystem<SubsystemTimeOfDay>(true);
        CreatureSpawn = Project.FindSubsystem<SubsystemCreatureSpawn>(false);
        WorkType = TravelMapWorkType.Local;
        // In the single-player API every attached player is a local, on-screen player. The
        // NetMod's "main player" gate existed only to exclude remote players that have no GUI on
        // this screen; there are none here, so any player that owns a ComponentGui is the map's
        // main player. (PlayerData.PlayerIndex is unreliable and is not 1-based the way the
        // multiplayer build assumed.)
        var hasScreen = player.ComponentGui is not null;
        RuntimeContext = new TravelMapRuntimeContext(
            WorkType,
            hasScreen,
            hasScreen);
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
            new GameUpdateTeleportPositionCommitter(
                _dispatcher,
                static () => { }),
            clock,
            TeleportDiagnosticReporter.Report,
            TeleportDiagnosticReporter.ReportSearch);
    }

    private static string ResolveWritableDataRoot()
    {
        // Store writable data under data:/ (a per-user writable location on every platform)
        // instead of app:/ (the game's install directory), which is READ-ONLY on Android and
        // raised "Access denied" — failing activation so no HUD appeared. Best-effort migrate an
        // existing app:/ tree from older desktop installs; copy (not move) so originals survive.
        var dataRoot = Engine.Storage.GetSystemPath("data:/SurvivalcraftTravelMap");
        try
        {
            var legacyRoot = Engine.Storage.GetSystemPath("app:/SurvivalcraftTravelMap");
            if (!string.Equals(legacyRoot, dataRoot, StringComparison.Ordinal)
                && Directory.Exists(legacyRoot)
                && !Directory.Exists(dataRoot))
            {
                CopyDirectoryContents(legacyRoot, dataRoot);
            }
        }
        catch (Exception)
        {
            // Migration is best-effort. Some runtimes (e.g. Android) throw even when merely
            // resolving an app:/ path, so swallow everything and fall back to a fresh data:/ tree.
        }

        return dataRoot;
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryContents(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private void InitializeUiSettings(UiInitializationState state)
    {
        if (!TravelMapRuntimePolicy.CreatesUi(RuntimeContext))
        {
            return;
        }

        _uiActions = new TrackedUiActionRunner(
            _ => ShowMessage(
                TravelMapText.Get("mapActionFailed", "地图操作未能完成"),
                TravelMapNoticeKind.Failure));
        state.AppRoot = ResolveWritableDataRoot();
        string? legacySettingsPath = null;
        try
        {
            legacySettingsPath = Engine.Storage.GetSystemPath("app:/GPSSetting.xml");
        }
        catch
        {
            // app:/ can be inaccessible (e.g. Android); skip the legacy GPSSetting.xml migration.
        }

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
            _settings = TravelMapSettings.CreateDefaults();
            _settingsStore.EnterReadOnlyMode();
            WarnPersistenceOnce(TravelMapText.Format(
                "settingsUnavailableFormat",
                "地图设置不可写，本次使用内存默认值：{0}",
                exception.Message));
        }
    }

    private void InitializeUiPersistence(UiInitializationState state)
    {
        if (!TravelMapRuntimePolicy.CreatesUi(RuntimeContext) || _settings is null)
        {
            return;
        }

        var gameInfo = Project.FindSubsystem<SubsystemGameInfo>(true);
        var identity = new TravelMapStorageIdentityInput(
            TravelMapStorageScope.LocalWorld,
            state.AppRoot,
            gameInfo.DirectoryName,
            null,
            null,
            gameInfo.DirectoryName,
            DeterministicPlayerGuid(Player.PlayerData.PlayerIndex));
        if (!TravelMapStorageIdentity.TryResolve(identity, out var storage, out var identityError))
        {
            Engine.Log.Warning($"[TravelMap] Persistence disabled: {identityError}");
            WarnPersistenceOnce(TravelMapText.Get(
                "persistenceIdentityMissing",
                "缺少可靠的世界或玩家身份，旅行地图持久化已禁用"));
            throw new InvalidOperationException(
                $"Travel-map persistence identity is unavailable: {identityError}");
        }

        try
        {
            _tileStore = new ExplorationTileStore(Path.Combine(storage!.Directory, "tiles"));
            _caveStore = new CaveExplorationStore(Path.Combine(storage.Directory, "caves"));
            _mapViewState = new MapViewState();
            _waypointRepository = new WaypointRepository(storage.Directory);
            state.WaypointLoadOutcome = _waypointRepository.LoadAsync(_lifetimeCancellation.Token)
                .GetAwaiter()
                .GetResult();
            _waypoints = _waypointRepository.GetAll();
            _dismissedDeathStore = new DismissedDeathStore(storage.Directory);
            _dismissedDeathStore.LoadAsync(_lifetimeCancellation.Token)
                .GetAwaiter()
                .GetResult();
            _dismissedDeaths.Clear();
            _dismissedDeaths.UnionWith(_dismissedDeathStore.Dismissed);
            _currentPositionWaypointHandler = new CurrentPositionWaypointHandler(
                _waypointRepository,
                () =>
                {
                    var position = Player.ComponentBody.Position;
                    return new Vector3(position.X, position.Y, position.Z);
                });
            var surfaceSource = new TileStoreMapPixelSource(_tileStore);
            state.PixelSource = new MapViewPixelSource(
                surfaceSource,
                () => _mapViewState?.Mode ?? MapViewMode.Surface,
                () => _caveStore.GetPixelSource(_mapViewState?.CaveY ?? CaveLayer.CenterForY(64)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _tileStore = null;
            _caveStore = null;
            _mapViewState = null;
            _waypointRepository = null;
            _dismissedDeathStore = null;
            _dismissedDeaths.Clear();
            _currentPositionWaypointHandler = null;
            WarnPersistenceOnce(TravelMapText.Format(
                "persistenceUnavailableFormat",
                "地图与坐标点持久化不可用：{0}",
                exception.Message));
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

        if (state.PixelSource is null)
        {
            return;
        }

        _miniMap = new MiniMapRenderer(
            state.PixelSource,
            _settings,
            _settingsStore,
            GetPlayerPose,
            GetVisibleWaypoints,
            GetCreatureMarkers,
            GetLastDeathMarker,
            GetMapBrightness,
            IsMapInputBlocked,
            OpenLargeMap,
            message => ShowMessage(message),
            OpenLargeMapAtLastDeath);
        _miniMap.GameTimeProvider = GetGameTime;
        _miniMap.PreviousDeathProvider = GetPreviousDeathMarker;
        Player.GuiWidget.Children.Add(_miniMap);
        _openMapButton = new BevelledButtonWidget
        {
            Text = TravelMapText.Get("openMap", "地图"),
            Size = new Engine.Vector2(
                TravelMapOverlayLayout.OpenMapButtonSize.X,
                TravelMapOverlayLayout.OpenMapButtonSize.Y),
        };
        Player.GuiWidget.Children.Add(_openMapButton);
        _miniMapPlacementWidget = new MiniMapPlacementWidget(
            ConfirmMiniMapPlacement,
            CancelMiniMapPlacement);
        UpdateHudPositions();

        _largeMapDialog = new TravelMapDialog(
            state.PixelSource,
            _settings,
            _settingsStore,
            GetPlayerPose,
            GetVisibleWaypoints,
            GetCreatureMarkers,
            GetMapBrightness,
            GetGameTime,
            HandleContextActionAsync,
            ShowMessage,
            BeginMiniMapPlacement,
            GetLastDeathMarker,
            _mapViewState,
            GetPreviousDeathMarker);
        if (state.WaypointLoadOutcome == WaypointLoadOutcome.CorruptIsolated)
        {
            ShowMessage(
                TravelMapText.Get("waypointFileCorrupt", "坐标点文件已损坏，已隔离并使用空列表"),
                TravelMapNoticeKind.Failure);
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
                var terrainSource = new SurvivalcraftTerrainMapSource(Terrain);
                var sampler = new TerrainMapSampler(terrainSource, stream);
                _explorationRecorder = new ExplorationRecorder(sampler, _tileStore);
                _caveSampler = new CaveMapSampler(terrainSource, sampler);
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
            _settings.MiniMapSize,
            _settings.MiniMapAnchorX,
            _settings.MiniMapAnchorY);
        if (_miniMapPlacementSession is not null)
        {
            positions = positions with { MiniMap = _miniMapPlacementSession.PreviewPosition };
        }

        if (_miniMap is not null)
        {
            SetHudWidgetPosition(_miniMap, positions.MiniMap);
        }

        if (_openMapButton is not null)
        {
            SetHudWidgetPosition(_openMapButton, positions.OpenMapButton);
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

    private void UpdateOpenMapButton()
    {
        // The always-visible open-map button is the touch-friendly counterpart to the
        // "M" hotkey: phones have no keyboard, so this is their primary way in. It also
        // covers players who hid the mini map (which otherwise doubles as a tap target).
        if (_openMapButton is null || !_openMapButton.IsEnabled)
        {
            return;
        }

        if (_openMapButton.IsClicked)
        {
            OpenLargeMap();
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
            HasOtherPlayers: false,
            InvitationFeatureAvailable: false,
            HasTextEntryFocus: !GetMapInputFocus().AllowsMapHotkey);
    }

    private void ApplyHudState(TravelMapHudState state)
    {
        var isPlacingMiniMap = _miniMapPlacementSession is not null;
        if (_miniMap is not null)
        {
            _miniMap.IsVisible = isPlacingMiniMap || state.ShowMiniMap;
            _miniMap.IsEnabled = !isPlacingMiniMap && state.AllowMiniMapInput;
        }

        if (_openMapButton is not null)
        {
            _openMapButton.IsVisible = !isPlacingMiniMap && state.ShowOpenMapButton;
            _openMapButton.IsEnabled = !isPlacingMiniMap
                && state.ShowOpenMapButton
                && state.AllowOpenMapInput;
        }
    }

    private void BeginMiniMapPlacement()
    {
        if (_settings is null || _miniMap is null || _miniMapPlacementWidget is null)
        {
            return;
        }

        var guiSize = Player.GuiWidget.ActualSize;
        var positions = TravelMapOverlayLayout.PlaceHud(
            new Vector2(guiSize.X, guiSize.Y),
            _settings.MiniMapSize,
            _settings.MiniMapAnchorX,
            _settings.MiniMapAnchorY);
        _miniMapPlacementSession = new MiniMapPlacementSession(positions.MiniMap);
        if (_largeMapDialog is not null && DialogsManager.Dialogs.Contains(_largeMapDialog))
        {
            DialogsManager.HideDialog(_largeMapDialog);
        }

        DialogsManager.ShowDialog(Player.GuiWidget, _miniMapPlacementWidget);
        ApplyHudState(TravelMapHudPolicy.Evaluate(GetHudSignals()));
        UpdateHudPositions();
        Player.GameWidget.Input.Clear();
    }

    private void UpdateMiniMapPlacement()
    {
        var session = _miniMapPlacementSession;
        if (session is null || _settings is null)
        {
            return;
        }

        var input = Player.GameWidget.Input;
        if (input.Cancel)
        {
            CancelMiniMapPlacement();
            return;
        }

        Engine.Vector2? startPointer = null;
        if (input.IsMouseButtonDownOnce(MouseButton.Left) && input.MousePosition.HasValue)
        {
            startPointer = input.MousePosition;
        }
        else if (input.Tap.HasValue)
        {
            startPointer = input.Tap;
        }

        if (startPointer.HasValue)
        {
            var local = Player.GuiWidget.ScreenToWidget(startPointer.Value);
            session.TryBeginDrag(new Vector2(local.X, local.Y), _settings.MiniMapSize);
        }

        Engine.Vector2? activePointer = null;
        if (input.IsMouseButtonDown(MouseButton.Left) && input.MousePosition.HasValue)
        {
            activePointer = input.MousePosition;
        }
        else if (input.Press.HasValue)
        {
            activePointer = input.Press;
        }

        if (session.IsDragging && activePointer.HasValue)
        {
            var local = Player.GuiWidget.ScreenToWidget(activePointer.Value);
            var guiSize = Player.GuiWidget.ActualSize;
            session.DragTo(
                new Vector2(local.X, local.Y),
                new Vector2(guiSize.X, guiSize.Y),
                _settings.MiniMapSize);
            UpdateHudPositions();
            input.Clear();
        }
        else if (session.IsDragging)
        {
            session.EndDrag();
        }
    }

    private void ConfirmMiniMapPlacement()
    {
        if (_miniMapPlacementSession is null || _settings is null)
        {
            return;
        }

        var guiSize = Player.GuiWidget.ActualSize;
        var anchor = _miniMapPlacementSession.CreateNormalizedAnchor(
            new Vector2(guiSize.X, guiSize.Y),
            _settings.MiniMapSize);
        _settings.MiniMapAnchorX = anchor.X;
        _settings.MiniMapAnchorY = anchor.Y;
        _miniMapPlacementSession = null;
        if (_miniMapPlacementWidget is not null)
        {
            DialogsManager.HideDialog(_miniMapPlacementWidget);
        }

        UpdateHudPositions();
        ApplyHudState(TravelMapHudPolicy.Evaluate(GetHudSignals()));
        _miniMapPlacementSaveTask = PersistMiniMapPlacementAsync();
        ShowMessage(
            TravelMapText.Get("miniMapPositionSaved", "小地图位置已保存"),
            TravelMapNoticeKind.Success);
        Player.GameWidget.Input.Clear();
    }

    private void CancelMiniMapPlacement()
    {
        if (_miniMapPlacementSession is null)
        {
            return;
        }

        _miniMapPlacementSession.Cancel();
        _miniMapPlacementSession = null;
        if (_miniMapPlacementWidget is not null)
        {
            DialogsManager.HideDialog(_miniMapPlacementWidget);
        }

        UpdateHudPositions();
        ApplyHudState(TravelMapHudPolicy.Evaluate(GetHudSignals()));
        ShowMessage(
            TravelMapText.Get("miniMapPositionCancelled", "已取消调整小地图位置"),
            TravelMapNoticeKind.Information);
        Player.GameWidget.Input.Clear();
    }

    private async Task PersistMiniMapPlacementAsync()
    {
        try
        {
            if (_settingsStore is not null && _settings is not null)
            {
                await _settingsStore.SaveAsync(_settings, _lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage(
                TravelMapText.Get(
                    "miniMapPositionSaveFailed",
                    "小地图位置未能保存，本次会话仍保留当前位置"),
                TravelMapNoticeKind.Failure);
        }
    }

    private void UpdateExploration()
    {
        if (_explorationRecorder is null || _explorationCoverageProbe is null)
        {
            return;
        }

        var position = Player.ComponentBody.Position;
        _mapViewState?.UpdatePlayerY(position.Y);
        var center = TerrainChunkCoordinate.FromWorld(
            checked((int)MathF.Floor(position.X)),
            checked((int)MathF.Floor(position.Z)));
        var loadedChunks = Terrain.Terrain.AllocatedChunks
            .Where(chunk => chunk is not null
                && SurvivalcraftTerrainMapSource.IsSurfaceReadable(chunk.State))
            .Select(chunk => new TerrainChunkCoordinate(chunk.Coords.X, chunk.Coords.Y))
            .Distinct()
            .OrderBy(chunk => DistanceSquared(chunk, center))
            .ThenBy(chunk => chunk.X)
            .ThenBy(chunk => chunk.Z)
            .ToArray();
        _explorationScheduler.ObserveChunks(center, loadedChunks);

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
                        TravelMapText.Get(
                            "mapStoragePaused",
                            "地图存储持续失败；已暂停记录新区块，现有探索仍会保留并重试保存"),
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

        UpdateCaveExploration(loadedChunks);
    }

    private void UpdateCaveExploration(IReadOnlyList<TerrainChunkCoordinate> loadedChunks)
    {
        if (_caveSampler is null
            || _caveStore is null
            || _explorationRecorder is null
            || _mapViewState?.Mode != MapViewMode.Cave)
        {
            return;
        }

        var layerY = _mapViewState.CaveY;
        Span<Rgba32> colors = stackalloc Rgba32[TerrainChunkCoordinate.PixelCount];
        Span<byte> heightShades = stackalloc byte[TerrainChunkCoordinate.PixelCount];
        var attempts = 0;
        foreach (var chunk in loadedChunks)
        {
            if (attempts >= MaximumCaveChunkAttemptsPerFrame)
            {
                break;
            }

            if (!_explorationRecorder.IsChunkFullyExplored(chunk)
                || _caveStore.IsChunkFullyExplored(layerY, chunk))
            {
                continue;
            }

            attempts++;
            if (!_caveSampler.TrySampleChunk(chunk, layerY, colors, heightShades))
            {
                continue;
            }

            var result = _caveStore.RecordChunk(layerY, chunk, colors, heightShades);
            if (result == ExplorationRecordResult.Pressure && !_explorationPressureWarningShown)
            {
                _explorationPressureWarningShown = true;
                ShowMessage(
                    TravelMapText.Get("mapStoragePaused", "地图缓存持续失败，已暂停记录新区块；稍后探索会自动重试保存"),
                    TravelMapNoticeKind.Failure);
                break;
            }
        }
    }

    private static long DistanceSquared(
        TerrainChunkCoordinate left,
        TerrainChunkCoordinate right)
    {
        var dx = (long)left.X - right.X;
        var dz = (long)left.Z - right.Z;
        return dx * dx + dz * dz;
    }

    private static void LogExplorationFailure(string message)
    {
        Engine.Log.Warning(message);
    }

    private void UpdateFlush(float dt)
    {
        if (_tileStore is null && _caveStore is null)
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
        var flushInterval = _tileStore?.FlushInterval ?? _caveStore!.FlushInterval;
        if (_flushTask is null && _flushElapsed >= (float)flushInterval.TotalSeconds)
        {
            _flushElapsed = 0f;
            _flushTask = FlushMapStoresAsync(_lifetimeCancellation.Token);
        }
    }

    private async Task FlushMapStoresAsync(CancellationToken cancellationToken)
    {
        if (_tileStore is not null)
        {
            await _tileStore.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_caveStore is not null)
        {
            await _caveStore.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    private void OpenLargeMapAtLastDeath(DeathMapMarker marker)
    {
        if (_largeMapDialog is null || !GetMapInputFocus().AllowsMapHotkey)
        {
            return;
        }

        _largeMapDialog.ResetToWorld(new Vector2(marker.Position.X, marker.Position.Z));
        DialogsManager.ShowDialog(Player.GuiWidget, _largeMapDialog);
        ApplyHudState(TravelMapHudPolicy.Evaluate(GetHudSignals()));
        Player.GameWidget.Input.Clear();
    }

    private TravelMapFocusState GetMapInputFocus(bool ignoreLargeMapDialog = false)
    {
        var hasDialogs = ignoreLargeMapDialog && _largeMapDialog is not null
            ? DialogsManager.Dialogs.Any(dialog => !ReferenceEquals(dialog, _largeMapDialog))
            : DialogsManager.HasDialogs(Player.GuiWidget);
        return TravelMapInputFocusEvaluator.Evaluate(new TravelMapInputFocusSignals(
            HasFocusedTextBox(Player.GuiWidget),
            false,
            false,
            Gui?.ModalPanelWidget is not null || hasDialogs));
    }

    private static Guid DeterministicPlayerGuid(int playerIndex)
    {
        // The API has no per-player GUID in single-player, so derive a stable, non-empty
        // GUID from the player index. This keeps each player's map/waypoint folder stable.
        Span<byte> bytes =
        [
            0x53, 0x43, 0x54, 0x4D, 0x54, 0x72, 0x61, 0x76,
            0x65, 0x6C, 0x4D, 0x61, 0x70, 0x00, 0x00, 0x00,
        ];
        BitConverter.TryWriteBytes(bytes[12..16], playerIndex);
        return new Guid(bytes);
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
                var target = new Vector3(
                    menu.WorldPosition.X,
                    menu.TargetY ?? Player.ComponentBody.Position.Y,
                    menu.WorldPosition.Y);
                return menu.TargetY.HasValue
                    ? await RequestWaypointTravelAsync(target, cancellationToken).ConfigureAwait(false)
                    : await RequestSurfaceTravelAsync(target, cancellationToken).ConfigureAwait(false);
            }
            case TravelMapContextAction.TeleportToWaypoint:
            {
                var waypoint = FindWaypoint(menu.WaypointId);
                return waypoint is null
                    ? TravelMapActionStatus.Failed
                    : await RequestWaypointTravelAsync(waypoint.Position, cancellationToken).ConfigureAwait(false);
            }
            case TravelMapContextAction.TeleportToLastDeath:
            {
                var lastDeath = GetLastDeathMarker();
                if (lastDeath is null)
                {
                    ShowMessage(
                        TravelMapText.Get("lastDeathUnavailable", "还没有可返回的死亡地点"),
                        TravelMapNoticeKind.Information);
                    return TravelMapActionStatus.FailedWithFeedback;
                }

                return await RequestWaypointTravelAsync(lastDeath.Position, cancellationToken)
                    .ConfigureAwait(false);
            }
            case TravelMapContextAction.DeleteLastDeath:
            {
                var identity = LatestDeathIdentity(Player.PlayerStats);
                if (identity is null || GetLastDeathMarker() is null)
                {
                    ShowMessage(
                        TravelMapText.Get("lastDeathUnavailable", "还没有可返回的死亡地点"),
                        TravelMapNoticeKind.Information);
                    return TravelMapActionStatus.FailedWithFeedback;
                }

                _dismissedDeaths.Add(identity.Value);
                await SaveDismissedDeathAsync(identity.Value, cancellationToken).ConfigureAwait(false);
                return TravelMapActionStatus.Completed;
            }
            case TravelMapContextAction.TeleportToPreviousDeath:
            {
                var previousDeath = GetPreviousDeathMarker();
                if (previousDeath is null)
                {
                    ShowMessage(
                        TravelMapText.Get("lastDeathUnavailable", "还没有可返回的死亡地点"),
                        TravelMapNoticeKind.Information);
                    return TravelMapActionStatus.FailedWithFeedback;
                }

                return await RequestWaypointTravelAsync(previousDeath.Position, cancellationToken)
                    .ConfigureAwait(false);
            }
            case TravelMapContextAction.DeletePreviousDeath:
            {
                var identity = PreviousDeathIdentity(Player.PlayerStats);
                if (identity is null || GetPreviousDeathMarker() is null)
                {
                    ShowMessage(
                        TravelMapText.Get("lastDeathUnavailable", "还没有可返回的死亡地点"),
                        TravelMapNoticeKind.Information);
                    return TravelMapActionStatus.FailedWithFeedback;
                }

                _dismissedDeaths.Add(identity.Value);
                await SaveDismissedDeathAsync(identity.Value, cancellationToken).ConfigureAwait(false);
                return TravelMapActionStatus.Completed;
            }
            case TravelMapContextAction.AddWaypoint:
            {
                if (_currentPositionWaypointHandler is null)
                {
                    return TravelMapActionStatus.Failed;
                }

                _waypoints = await _currentPositionWaypointHandler.SaveAsync(cancellationToken)
                    .ConfigureAwait(false);
                ShowMessage(
                    TravelMapText.Get("waypointSaved", "坐标点已保存"),
                    TravelMapNoticeKind.Success);
                return TravelMapActionStatus.Completed;
            }
            case TravelMapContextAction.RenameWaypoint:
            {
                var waypoint = FindWaypoint(menu.WaypointId);
                if (waypoint is null)
                {
                    return TravelMapActionStatus.Failed;
                }

                DialogsManager.ShowDialog(
                    Player.GuiWidget,
                    new TextBoxDialog(
                        TravelMapText.Get("renameWaypoint", "重命名坐标点"),
                        waypoint.Name,
                        64,
                        name =>
                        {
                            if (name is not null)
                            {
                                _uiActions?.TryRun(_ => RenameWaypointAsync(waypoint.Id, name));
                            }
                        }));
                return TravelMapActionStatus.Completed;
            }
            case TravelMapContextAction.DeleteWaypoint:
                if (!menu.WaypointId.HasValue || !_waypointRepository.Remove(menu.WaypointId.Value))
                {
                    return TravelMapActionStatus.Failed;
                }

                await SaveWaypointsAsync(cancellationToken).ConfigureAwait(false);
                ShowMessage(
                    TravelMapText.Get("waypointDeleted", "坐标点已删除"),
                    TravelMapNoticeKind.Success);
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
            null,
            null);
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
            null,
            null);
        return ToActionStatus(await router.RequestWaypointAsync(target, cancellationToken).ConfigureAwait(false));
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
                ShowMessage(
                    TravelMapText.Get("waypointRenamed", "坐标点已重命名"),
                    TravelMapNoticeKind.Success);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            ShowMessage(
                TravelMapText.Get("waypointRenameFailed", "坐标点名称未保存"),
                TravelMapNoticeKind.Failure);
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

    private async Task SaveDismissedDeathAsync(
        DeathMarkerIdentity identity,
        CancellationToken cancellationToken)
    {
        if (_dismissedDeathStore is null)
        {
            return;
        }

        await _dismissedDeathStore.AddAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    private PlayerMapPose GetPlayerPose()
    {
        var position = Player.ComponentBody.Position;
        var forward = Player.ComponentBody.Matrix.Forward;
        return new PlayerMapPose(
            new Vector3(position.X, position.Y, position.Z),
            MathF.Atan2(forward.X, -forward.Z));
    }

    private DeathMapMarker? GetLastDeathMarker() =>
        SelectCurrentDeath(Player.PlayerStats, _dismissedDeaths);

    private DeathMapMarker? GetPreviousDeathMarker() =>
        SelectPreviousDeath(Player.PlayerStats, _dismissedDeaths);

    /// <summary>
    /// The tracked "current" death: the newest record unless the player dismissed exactly that
    /// record. A later death (a newer record) is a distinct identity and re-shows a marker.
    /// </summary>
    internal static DeathMapMarker? SelectCurrentDeath(
        PlayerStats? stats,
        IReadOnlySet<DeathMarkerIdentity>? dismissedDeaths)
    {
        if (stats is null)
        {
            return null;
        }

        var records = stats.DeathRecords;
        if (records.Count == 0)
        {
            return null;
        }

        var record = records[records.Count - 1];
        if (dismissedDeaths is not null && dismissedDeaths.Contains(IdentityOf(record)))
        {
            return null;
        }

        return ToMarker(record);
    }

    /// <summary>
    /// The "previous" death: the second-to-last record, shown regardless of whether the current
    /// death was dismissed, unless the player individually dismissed this previous record too.
    /// </summary>
    internal static DeathMapMarker? SelectPreviousDeath(
        PlayerStats? stats,
        IReadOnlySet<DeathMarkerIdentity>? dismissedDeaths)
    {
        if (stats is null)
        {
            return null;
        }

        var records = stats.DeathRecords;
        if (records.Count < 2)
        {
            return null;
        }

        var record = records[records.Count - 2];
        if (dismissedDeaths is not null && dismissedDeaths.Contains(IdentityOf(record)))
        {
            return null;
        }

        return ToMarker(record);
    }

    /// <summary>Identity of the newest death record, used when dismissing the current death.</summary>
    internal static DeathMarkerIdentity? LatestDeathIdentity(PlayerStats? stats)
    {
        if (stats is null)
        {
            return null;
        }

        var records = stats.DeathRecords;
        return records.Count == 0 ? null : IdentityOf(records[records.Count - 1]);
    }

    /// <summary>Identity of the second-to-last death record, used when dismissing the previous death.</summary>
    internal static DeathMarkerIdentity? PreviousDeathIdentity(PlayerStats? stats)
    {
        if (stats is null)
        {
            return null;
        }

        var records = stats.DeathRecords;
        return records.Count < 2 ? null : IdentityOf(records[records.Count - 2]);
    }

    private static DeathMarkerIdentity IdentityOf(PlayerStats.DeathRecord record) =>
        DeathMarkerIdentity.FromLocation(
            record.Day,
            new Vector3(record.Location.X, record.Location.Y, record.Location.Z));

    private static DeathMapMarker? ToMarker(PlayerStats.DeathRecord record)
    {
        var position = new Vector3(record.Location.X, record.Location.Y, record.Location.Z);
        return float.IsFinite(position.X)
            && float.IsFinite(position.Y)
            && float.IsFinite(position.Z)
                ? new DeathMapMarker(position, record.Day, record.Cause ?? string.Empty)
                : null;
    }

    private IReadOnlyList<CreatureMapMarker> GetCreatureMarkers()
    {
        if (CreatureSpawn is null)
        {
            return [];
        }

        var markers = new List<CreatureMapMarker>();
        foreach (var creature in CreatureSpawn.Creatures)
        {
            if (ReferenceEquals(creature, Player)
                || creature.ComponentBody is null
                || creature.ComponentHealth is { Health: <= 0f })
            {
                continue;
            }

            var position = creature.ComponentBody.Position;
            if (_mapViewState is { Mode: MapViewMode.Cave } caveView
                && MathF.Abs(position.Y - caveView.CaveY) > CaveLayer.MarkerVerticalRange)
            {
                continue;
            }

            markers.Add(new CreatureMapMarker(
                new Vector3(position.X, position.Y, position.Z),
                ToCreatureMarkerKind(creature.Category)));
        }

        return markers;
    }

    private IReadOnlyList<Waypoint> GetVisibleWaypoints() =>
        _mapViewState is { Mode: MapViewMode.Cave } caveView
            ? _waypoints
                .Where(waypoint => MathF.Abs(waypoint.Position.Y - caveView.CaveY)
                    <= CaveLayer.MarkerVerticalRange)
                .ToArray()
            : _waypoints;

    internal static CreatureMapMarkerKind ToCreatureMarkerKind(CreatureCategory category)
    {
        if ((category & (CreatureCategory.LandPredator | CreatureCategory.WaterPredator)) != 0)
        {
            return CreatureMapMarkerKind.Predator;
        }

        return (category & CreatureCategory.Bird) != 0
            ? CreatureMapMarkerKind.Bird
            : CreatureMapMarkerKind.Other;
    }

    private float GetTerrainBrightness() => _settings is { UseDayNightTint: true }
        ? DayNightBrightness.Calculate(TimeOfDay.TimeOfDay, _settings.NightMinimumBrightness)
        : 1f;

    private float GetMapBrightness() => _mapViewState?.Mode == MapViewMode.Cave
        ? 1f
        : GetTerrainBrightness();

    private float GetGameTime() => TimeOfDay.TimeOfDay;

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
        _explorationFailureReporter.Clear();
        RunCleanupStep(() => _lifetimeCancellation.Cancel());
        RunCleanupStep(() => _chunkLoader?.Dispose());
        RunCleanupStep(CleanupUi);
        RunCleanupStep(() => _dispatcher?.Dispose());
        _chunkLoader = null;
        _dispatcher = null;
        TeleportService = null;
        _settingsStore = null;
        _settings = null;
        _tileStore = null;
        _caveStore = null;
        _explorationRecorder = null;
        _caveSampler = null;
        _explorationCoverageProbe = null;
        _mapViewState = null;
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
        var uiActions = _uiActions;
        _uiActions = null;
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
        var placementSaveWork = _miniMapPlacementSaveTask;
        _miniMapPlacementSaveTask = null;
        _miniMapPlacementSession = null;
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

        if (_miniMapPlacementWidget is not null)
        {
            var placementWidget = _miniMapPlacementWidget;
            _miniMapPlacementWidget = null;
            RunCleanupStep(() => DialogsManager.HideDialog(placementWidget));
            RunCleanupStep(placementWidget.Dispose);
        }

        if (_openMapButton is not null)
        {
            var openMapButton = _openMapButton;
            _openMapButton = null;
            RunCleanupStep(() => openMapButton.ParentWidget?.Children.Remove(openMapButton));
            RunCleanupStep(openMapButton.Dispose);
        }

        if (_tileStore is not null || _caveStore is not null)
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
                        FlushMapStoresAsync(flushCancellation.Token),
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

        if (placementSaveWork is not null && RemainingTime() > TimeSpan.Zero)
        {
            RunCleanupStep(() => BoundedTaskObserver.ObserveWithin(
                    placementSaveWork,
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
