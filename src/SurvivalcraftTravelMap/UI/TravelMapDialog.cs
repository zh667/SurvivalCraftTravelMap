using Engine;
using Engine.Input;
using Game;
using SurvivalcraftTravelMap.Map;
using SurvivalcraftTravelMap.Settings;
using SurvivalcraftTravelMap.Waypoints;
using NVector2 = System.Numerics.Vector2;

namespace SurvivalcraftTravelMap.UI;

public enum TravelMapActionStatus
{
    Completed,
    Cancelled,
    Unavailable,
    Failed,
    FailedWithFeedback,
}

public delegate Task<TravelMapActionStatus> TravelMapContextActionHandler(
    TravelMapContextAction action,
    TravelMapContextMenu menu,
    CancellationToken cancellationToken);

public enum TravelMapDialogCancelAction
{
    CloseSettings,
    CloseDialog,
}

public static class TravelMapDialogCancelPolicy
{
    public static TravelMapDialogCancelAction Resolve(bool settingsVisible) =>
        settingsVisible
            ? TravelMapDialogCancelAction.CloseSettings
            : TravelMapDialogCancelAction.CloseDialog;
}

public sealed class TravelMapDialog : Dialog
{
    private static readonly Color Basalt = new(0x1B, 0x26, 0x28, 0xFF);
    private static readonly Color Moss = new(0x6F, 0x8A, 0x3B, 0xFF);
    private static readonly Color SurveyCyan = new(0x74, 0xC9, 0xC8, 0xFF);
    private static readonly Color HazardAmber = new(0xE2, 0xA3, 0x3B, 0xFF);
    private static readonly Color SnowText = new(0xE8, 0xEC, 0xE7, 0xFF);

    private readonly TravelMapUiController _controller = new();
    private readonly TravelMapSettings _settings;
    private readonly TravelMapSettingsStore _settingsStore;
    private readonly Func<PlayerMapPose> _playerPose;
    private readonly Func<DeathMapMarker?> _lastDeath;
    private readonly Func<DeathMapMarker?> _previousDeath;
    private readonly Func<float> _gameTime;
    private readonly MapViewState _mapViewState;
    private readonly Action<TravelMapNotice> _notify;
    private readonly TravelMapContextActionHandler _actionHandler;
    private readonly TrackedUiActionRunner _actionRunner;
    private readonly TrackedUiActionRunner _persistenceRunner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RectangleWidget _background;
    private readonly CanvasWidget _mapHost;
    private readonly CanvasWidget _mapInformationHost;
    private readonly MapSurfaceWidget _surface;
    private readonly CanvasWidget _topBar;
    private readonly LabelWidget _scaleLabel;
    private readonly LabelWidget _coordinateLabel;
    private readonly LabelWidget _timeLabel;
    private readonly BevelledButtonWidget _locateButton;
    private readonly BevelledButtonWidget _savePlayerButton;
    private readonly BevelledButtonWidget _settingsButton;
    private readonly BevelledButtonWidget _closeButton;
    private readonly BevelledButtonWidget _returnToDeathButton;
    private readonly CanvasWidget _mapModeHost;
    private readonly BevelledButtonWidget _surfaceModeButton;
    private readonly BevelledButtonWidget _caveModeButton;
    private readonly BevelledButtonWidget _caveYDownButton;
    private readonly BevelledButtonWidget _caveYUpButton;
    private readonly BevelledButtonWidget _caveFollowYButton;
    private readonly BevelledButtonWidget _caveYLabel;
    private readonly TravelMapSettingsWidget _settingsWidget;
    private readonly CanvasWidget _contextCard;
    private readonly StackPanelWidget _contextActions;
    private readonly TravelMapNoticeController _noticeController =
        new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));
    private readonly LargeMapFollowState _followState = new();
    private readonly TouchMapGestureState _touchGesture = new();
    private readonly MiniMapTouchTapState _deathTouchTap = new();
    private readonly TouchMapLongPressState _touchLongPress = new();
    private readonly CanvasWidget _noticeHost;
    private readonly RectangleWidget _noticeBackground;
    private readonly LabelWidget _noticeLabel;
    private readonly List<(BevelledButtonWidget Button, TravelMapContextAction Action)> _contextButtons = [];
    private TravelMapContextMenu? _activeMenu;
    private NVector2? _lastDragPosition;
    private float _lastScale = float.NaN;
    private bool _scaleSavePending;
    private double _scaleSaveTime;
    private (int X, int Y, int Z) _lastTopCoordinate;
    private int _lastTopMinute = -1;
    private bool? _lastShowCoordinates;
    private bool? _lastShowGameTime;
    private string _topCoordinateText = string.Empty;
    private string _topTimeText = string.Empty;

    public TravelMapDialog(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        TravelMapSettingsStore settingsStore,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<IReadOnlyList<CreatureMapMarker>> creatures,
        Func<float> brightness,
        Func<float> gameTime,
        TravelMapContextActionHandler actionHandler,
        Action<TravelMapNotice> notify,
        Action requestMiniMapPlacement,
        Func<DeathMapMarker?>? lastDeath = null,
        MapViewState? mapViewState = null,
        Func<DeathMapMarker?>? previousDeath = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _playerPose = playerPose ?? throw new ArgumentNullException(nameof(playerPose));
        _lastDeath = lastDeath ?? (() => null);
        _previousDeath = previousDeath ?? (() => null);
        _mapViewState = mapViewState ?? new MapViewState();
        _gameTime = gameTime ?? throw new ArgumentNullException(nameof(gameTime));
        _actionHandler = actionHandler ?? throw new ArgumentNullException(nameof(actionHandler));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _actionRunner = new TrackedUiActionRunner(
            _ => Notify(
                TravelMapText.Get("mapActionFailed", "地图操作未能完成"),
                TravelMapNoticeKind.Failure));
        _persistenceRunner = new TrackedUiActionRunner(
            _ => Notify(
                TravelMapText.Get(
                    "largeMapZoomSaveFailedSession",
                    "大地图比例未能保存，本次会话将保留当前值"),
                TravelMapNoticeKind.Failure));

        _background = new RectangleWidget
        {
            FillColor = Basalt,
            OutlineColor = Color.Transparent,
        };
        Children.Add(_background);

        _mapHost = new CanvasWidget { ClampToBounds = true };
        Children.Add(_mapHost);
        _surface = new MapSurfaceWidget(
            pixelSource,
            settings,
            playerPose,
            waypoints,
            creatures,
            _lastDeath,
            brightness)
        {
            GameTimeProvider = _gameTime,
            ShowMapInformation = false,
            AutoCenterOnPlayer = false,
            ShowWaypointLabels = true,
            ShowSurveyCrosshair = false,
            PlayerMarkerColor = TravelMapPalette.MiniMapPlayer,
            DrawPlayerOutline = true,
            PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(settings.MiniMapSize),
            Transform = new MapTransform(
                new NVector2(playerPose().Position.X, playerPose().Position.Z),
                settings.LargeMapBlocksPerPixel,
                NVector2.One),
        };
        _surface.PreviousDeathProvider = _previousDeath;
        _mapHost.Children.Add(_surface);

        _mapInformationHost = new CanvasWidget
        {
            Size = new Vector2(320f, 44f),
            ClampToBounds = true,
        };
        Children.Add(_mapInformationHost);

        _timeLabel = new LabelWidget
        {
            Color = SnowText,
            FontScale = 0.72f,
            Size = new Vector2(320f, 22f),
            TextAnchor = Engine.Graphics.TextAnchor.VerticalCenter,
        };
        _mapInformationHost.Children.Add(_timeLabel);

        _coordinateLabel = new LabelWidget
        {
            Color = SnowText,
            FontScale = 0.72f,
            Size = new Vector2(320f, 22f),
            TextAnchor = Engine.Graphics.TextAnchor.VerticalCenter,
        };
        _mapInformationHost.Children.Add(_coordinateLabel);
        _mapInformationHost.SetWidgetPosition(_coordinateLabel, new Vector2(0f, 22f));

        _topBar = new CanvasWidget();
        Children.Add(_topBar);
        _topBar.Children.Add(new RectangleWidget
        {
            FillColor = new Color(Basalt, 245),
            OutlineColor = Moss,
            OutlineThickness = 1f,
        });

        var title = new LabelWidget
        {
            Text = TravelMapText.Get("largeMapTitle", "旅行地图"),
            Color = SnowText,
            FontScale = 0.95f,
            Size = new Vector2(330f, 44f),
            TextAnchor = Engine.Graphics.TextAnchor.VerticalCenter,
        };
        _topBar.Children.Add(title);
        _topBar.SetWidgetPosition(title, new Vector2(16f, 0f));

        _scaleLabel = new LabelWidget
        {
            Color = SurveyCyan,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            Size = new Vector2(250f, 44f),
            TextAnchor = Engine.Graphics.TextAnchor.VerticalCenter,
        };
        _topBar.Children.Add(_scaleLabel);
        _topBar.SetWidgetPosition(_scaleLabel, new Vector2(350f, 0f));

        _locateButton = CreateButton(TravelMapText.Get("followPlayer", "跟随玩家"), 112f);
        _savePlayerButton = CreateButton(TravelMapText.Get("saveCurrentPosition", "保存当前位置"), 132f);
        _settingsButton = CreateButton(TravelMapText.Get("settings", "设置"), 92f);
        _closeButton = CreateButton(TravelMapText.Get("close", "关闭"), 92f);
        _topBar.Children.Add(_savePlayerButton);
        _topBar.Children.Add(_locateButton);
        _topBar.Children.Add(_settingsButton);
        _topBar.Children.Add(_closeButton);

        _returnToDeathButton = CreateButton(
            TravelMapText.Get("returnToLastDeath", "返回死亡点"),
            170f);
        _returnToDeathButton.IsVisible = false;
        Children.Add(_returnToDeathButton);

        _mapModeHost = new CanvasWidget
        {
            Size = new Vector2(500f, 88f),
        };
        Children.Add(_mapModeHost);
        _surfaceModeButton = CreateButton(TravelMapText.Get("surfaceMode", "地表"), 90f);
        _caveModeButton = CreateButton(TravelMapText.Get("caveMode", "洞穴"), 90f);
        _caveYDownButton = CreateButton("Y−", 70f);
        _caveYUpButton = CreateButton("Y+", 70f);
        _caveFollowYButton = CreateButton(TravelMapText.Get("followPlayerY", "跟随玩家Y轴"), 150f);
        _caveYLabel = CreateButton($"Y: {_mapViewState.CaveY}", 110f);
        _mapModeHost.Children.Add(_surfaceModeButton);
        _mapModeHost.Children.Add(_caveModeButton);
        _mapModeHost.Children.Add(_caveYDownButton);
        _mapModeHost.Children.Add(_caveYLabel);
        _mapModeHost.Children.Add(_caveYUpButton);
        _mapModeHost.Children.Add(_caveFollowYButton);
        _mapModeHost.SetWidgetPosition(_surfaceModeButton, new Vector2(0f, 0f));
        _mapModeHost.SetWidgetPosition(_caveModeButton, new Vector2(96f, 0f));
        _mapModeHost.SetWidgetPosition(_caveYDownButton, new Vector2(0f, 46f));
        _mapModeHost.SetWidgetPosition(_caveYLabel, new Vector2(76f, 48f));
        _mapModeHost.SetWidgetPosition(_caveYUpButton, new Vector2(192f, 46f));
        _mapModeHost.SetWidgetPosition(_caveFollowYButton, new Vector2(268f, 46f));

        _settingsWidget = new TravelMapSettingsWidget(
            settings,
            settingsStore,
            notice => Notify(notice.Text, notice.Kind),
            CloseSettings,
            () =>
            {
                CloseSettings();
                requestMiniMapPlacement();
            })
        {
            IsVisible = false,
        };
        Children.Add(_settingsWidget);

        _contextCard = new CanvasWidget
        {
            Size = new Vector2(240f, 230f),
            IsVisible = false,
        };
        Children.Add(_contextCard);
        _contextCard.Children.Add(new RectangleWidget
        {
            Size = _contextCard.Size,
            FillColor = new Color(Basalt, 248),
            OutlineColor = HazardAmber,
            OutlineThickness = 2f,
        });
        var contextTitle = new LabelWidget
        {
            Text = TravelMapText.Get("mapPointActions", "测量点操作"),
            Color = HazardAmber,
            FontScale = 0.8f,
            Size = new Vector2(210f, 38f),
            TextAnchor = Engine.Graphics.TextAnchor.VerticalCenter,
        };
        _contextCard.Children.Add(contextTitle);
        _contextCard.SetWidgetPosition(contextTitle, new Vector2(14f, 6f));
        _contextActions = new StackPanelWidget
        {
            Direction = LayoutDirection.Vertical,
            Margin = new Vector2(3f),
        };
        _contextCard.Children.Add(_contextActions);
        _contextCard.SetWidgetPosition(_contextActions, new Vector2(12f, 46f));

        _noticeHost = new CanvasWidget
        {
            Size = new Vector2(560f, 48f),
            IsVisible = false,
        };
        Children.Add(_noticeHost);
        _noticeBackground = new RectangleWidget
        {
            Size = _noticeHost.Size,
            FillColor = new Color(Basalt, 238),
            OutlineColor = SnowText,
            OutlineThickness = 2f,
        };
        _noticeHost.Children.Add(_noticeBackground);
        _noticeLabel = new LabelWidget
        {
            Size = _noticeHost.Size,
            Color = SnowText,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            TextAnchor = Engine.Graphics.TextAnchor.HorizontalCenter
                | Engine.Graphics.TextAnchor.VerticalCenter,
        };
        _noticeHost.Children.Add(_noticeLabel);
        RefreshScaleText();
    }

    public override void ArrangeOverride()
    {
        _background.Size = ActualSize;
        _mapHost.Size = new Vector2(
            MathF.Max(1f, ActualSize.X - 24f),
            MathF.Max(1f, ActualSize.Y - 68f));
        SetWidgetPosition(_mapHost, new Vector2(12f, 56f));
        var mapInformationWidth = MathF.Max(1f, MathF.Min(340f, _mapHost.Size.X - 16f));
        _mapInformationHost.Size = new Vector2(mapInformationWidth, 44f);
        _timeLabel.Size = new Vector2(mapInformationWidth, 22f);
        _coordinateLabel.Size = new Vector2(mapInformationWidth, 22f);
        SetWidgetPosition(_mapInformationHost, new Vector2(20f, 62f));
        _topBar.Size = new Vector2(ActualSize.X, 48f);
        var controlsStart = MathF.Max(0f, ActualSize.X - 324f);
        _topBar.SetWidgetPosition(_savePlayerButton, new Vector2(MathF.Max(0f, controlsStart - 138f), 3f));
        _topBar.SetWidgetPosition(_locateButton, new Vector2(controlsStart, 3f));
        _topBar.SetWidgetPosition(_settingsButton, new Vector2(MathF.Max(0f, ActualSize.X - 204f), 3f));
        _topBar.SetWidgetPosition(_closeButton, new Vector2(MathF.Max(0f, ActualSize.X - 104f), 3f));
        SetWidgetPosition(
            _returnToDeathButton,
            new Vector2(MathF.Max(20f, ActualSize.X - 190f), MathF.Max(58f, ActualSize.Y - 54f)));
        SetWidgetPosition(_mapModeHost, new Vector2(20f, MathF.Max(58f, ActualSize.Y - 106f)));
        var noticeWidth = MathF.Max(1f, MathF.Min(560f, ActualSize.X - 32f));
        _noticeHost.Size = new Vector2(noticeWidth, 48f);
        _noticeBackground.Size = _noticeHost.Size;
        _noticeLabel.Size = _noticeHost.Size;
        SetWidgetPosition(_noticeHost, new Vector2((ActualSize.X - noticeWidth) / 2f, 58f));
        base.ArrangeOverride();
    }

    public void ShowNotice(TravelMapNotice notice)
    {
        _noticeController.Show(notice, Time.FrameStartTime);
        _noticeLabel.Text = notice.Text;
        var color = notice.Kind switch
        {
            TravelMapNoticeKind.Success => SurveyCyan,
            TravelMapNoticeKind.Failure => HazardAmber,
            _ => SnowText,
        };
        _noticeLabel.Color = color;
        _noticeBackground.OutlineColor = color;
        _noticeHost.IsVisible = true;
    }

    public void ResetToPlayer()
    {
        var position = _playerPose().Position;
        _surface.Transform = _followState.Locate(
            _surface.Transform with
            {
                ViewportSize = new NVector2(
                    MathF.Max(1f, _surface.ActualSize.X),
                    MathF.Max(1f, _surface.ActualSize.Y)),
                BlocksPerPixel = _settings.LargeMapBlocksPerPixel,
            },
            new NVector2(position.X, position.Z));
        HideContextMenu();
        _settingsWidget.IsVisible = false;
        _noticeController.Clear();
        _noticeHost.IsVisible = false;
        RefreshScaleText();
    }

    public void ResetToWorld(NVector2 worldPosition)
    {
        _surface.Transform = _followState.LocateTarget(
            _surface.Transform with
            {
                ViewportSize = new NVector2(
                    MathF.Max(1f, _surface.ActualSize.X),
                    MathF.Max(1f, _surface.ActualSize.Y)),
                BlocksPerPixel = _settings.LargeMapBlocksPerPixel,
            },
            worldPosition);
        HideContextMenu();
        _settingsWidget.IsVisible = false;
        RefreshScaleText();
    }

    public override void Update()
    {
        if (!_noticeController.Update(Time.FrameStartTime))
        {
            _noticeHost.IsVisible = false;
        }

        _surface.PlayerArrowSize = TravelMapRenderModel.MiniMapPlayerArrowSize(
            _settings.MiniMapSize);
        var lastDeath = _settings.ShowLastDeathMarker ? _lastDeath() : null;
        _returnToDeathButton.IsVisible = lastDeath is not null;
        _mapModeHost.IsVisible = !_settingsWidget.IsVisible;
        RefreshMapModeControls();
        RefreshTopInformation();
        var livePosition = _playerPose().Position;
        _surface.Transform = _followState.Update(
            _surface.Transform,
            new NVector2(livePosition.X, livePosition.Z));
        _locateButton.Text = _followState.IsFollowing
            ? TravelMapText.Get("followingPlayer", "跟随中")
            : TravelMapText.Get("followPlayer", "跟随玩家");

        if (_scaleSavePending && Time.FrameStartTime >= _scaleSaveTime)
        {
            PersistScale();
        }

        if (Input.Cancel)
        {
            if (TravelMapDialogCancelPolicy.Resolve(_settingsWidget.IsVisible)
                == TravelMapDialogCancelAction.CloseSettings)
            {
                CloseSettings();
                return;
            }

            PersistScale();
            DialogsManager.HideDialog(this);
            return;
        }

        if (_closeButton.IsClicked)
        {
            PersistScale();
            DialogsManager.HideDialog(this);
            return;
        }

        if (_locateButton.IsClicked)
        {
            LocatePlayer();
            return;
        }

        if (_savePlayerButton.IsClicked)
        {
            var pose = _playerPose();
            var menu = new TravelMapContextMenu(
                new NVector2(pose.Position.X, pose.Position.Z),
                null,
                [TravelMapContextAction.AddPlayerWaypoint]);
            if (!_actionRunner.TryRun(token => ExecuteActionAsync(
                    TravelMapContextAction.AddPlayerWaypoint,
                    menu,
                    token)))
            {
                Notify(
                    TravelMapText.Get("mapActionBusy", "另一项地图操作仍在执行"),
                    TravelMapNoticeKind.Information);
            }

            return;
        }

        if (_surfaceModeButton.IsClicked)
        {
            _mapViewState.ShowSurface();
            HideContextMenu();
            return;
        }

        if (_caveModeButton.IsClicked)
        {
            _mapViewState.ShowCave(_playerPose().Position.Y);
            HideContextMenu();
            return;
        }

        if (_caveYDownButton.IsClicked)
        {
            _mapViewState.StepCaveY(-1);
            HideContextMenu();
            return;
        }

        if (_caveYUpButton.IsClicked)
        {
            _mapViewState.StepCaveY(1);
            HideContextMenu();
            return;
        }

        if (_caveYLabel.IsClicked)
        {
            ShowCaveYInput();
            HideContextMenu();
            return;
        }

        if (_caveFollowYButton.IsClicked)
        {
            _mapViewState.FollowPlayer(_playerPose().Position.Y);
            HideContextMenu();
            return;
        }

        if (_returnToDeathButton.IsClicked && lastDeath is not null)
        {
            var menu = new TravelMapContextMenu(
                new NVector2(lastDeath.Position.X, lastDeath.Position.Z),
                null,
                [TravelMapContextAction.TeleportToLastDeath]);
            if (!_actionRunner.TryRun(token => ExecuteActionAsync(
                    TravelMapContextAction.TeleportToLastDeath,
                    menu,
                    token)))
            {
                Notify(
                    TravelMapText.Get("mapActionBusy", "另一项地图操作仍在执行"),
                    TravelMapNoticeKind.Information);
            }

            return;
        }

        if (_settingsButton.IsClicked)
        {
            _settingsWidget.IsVisible = !_settingsWidget.IsVisible;
            HideContextMenu();
        }

        if (_settingsWidget.IsVisible)
        {
            _lastDragPosition = null;
            _touchGesture.Reset();
            _deathTouchTap.Reset();
            return;
        }

        if (_settings.LargeMapBlocksPerPixel != _surface.Transform.BlocksPerPixel)
        {
            _surface.Transform = _surface.Transform with
            {
                BlocksPerPixel = _settings.LargeMapBlocksPerPixel,
            };
            RefreshScaleText();
        }

        // Process context-menu buttons before the map touch gesture. On touch devices the gesture
        // handler consumes the touch and returns early, so leaving this after it meant a tap on a
        // long-press menu option never reached the buttons.
        foreach (var item in _contextButtons)
        {
            if (!item.Button.IsClicked)
            {
                continue;
            }

            if (item.Action == TravelMapContextAction.Cancel)
            {
                HideContextMenu();
            }
            else if (_activeMenu is not null)
            {
                var menu = _activeMenu;
                if (!_actionRunner.TryRun(token => ExecuteActionAsync(item.Action, menu, token)))
                {
                    Notify(
                        TravelMapText.Get("mapActionBusy", "另一项地图操作仍在执行"),
                        TravelMapNoticeKind.Information);
                }
            }

            return;
        }

        if (HandleTouchGesture())
        {
            _lastDragPosition = null;
            _surface.LabelPointer = null;
            return;
        }

        var pointer = Input.MousePosition;
        if (!pointer.HasValue)
        {
            _lastDragPosition = null;
            return;
        }

        var localEngine = _surface.ScreenToWidget(pointer.Value);
        var local = new NVector2(localEngine.X, localEngine.Y);
        var hovered = _surface.ContainsLocalPoint(local);
        _surface.LabelPointer = hovered ? local : null;

        if (hovered && Input.IsMouseButtonDownOnce(MouseButton.Left))
        {
            var clickedDeath = _surface.HitLastDeath(local) ?? _surface.HitPreviousDeath(local);
            if (clickedDeath is not null)
            {
                ResetToWorld(new NVector2(clickedDeath.Position.X, clickedDeath.Position.Z));
                Input.Clear();
                return;
            }
        }

        var wheelSteps = Input.MouseWheelMovement / 120f;
        var zoom = _controller.HandleWheel(
            _surface.Transform,
            local,
            wheelSteps,
            hovered,
            0.25f,
            32f);
        ApplyTransformCommand(zoom);

        if (hovered && Input.IsMouseButtonDown(MouseButton.Left))
        {
            if (_lastDragPosition.HasValue)
            {
                ApplyTransformCommand(_controller.HandlePan(
                    _surface.Transform,
                    local - _lastDragPosition.Value,
                    isDragging: true));
            }

            _lastDragPosition = local;
        }
        else
        {
            _lastDragPosition = null;
        }

        if (hovered && Input.IsMouseButtonDownOnce(MouseButton.Right))
        {
            OpenTeleportMenuAt(local, pointer.Value);
        }
    }

    public override void Dispose()
    {
        _actionRunner.Dispose();
        _persistenceRunner.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.Dispose();
    }

    internal Task WhenBackgroundWorkIdleAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            _actionRunner.WhenIdleAsync(cancellationToken),
            _persistenceRunner.WhenIdleAsync(cancellationToken),
            _settingsWidget.WhenSaveIdleAsync(cancellationToken));

    private void ApplyTransformCommand(TravelMapUiCommand command)
    {
        if (command.Transform is not { } transform)
        {
            return;
        }

        _followState.ObserveManualNavigation(command.Kind);
        _surface.Transform = transform;
        if (command.Kind == TravelMapUiCommandKind.Zoom
            && _settings.LargeMapBlocksPerPixel != transform.BlocksPerPixel)
        {
            _settings.LargeMapBlocksPerPixel = transform.BlocksPerPixel;
            _scaleSavePending = true;
            _scaleSaveTime = Time.FrameStartTime + 0.5;
        }

        RefreshScaleText();
    }

    private bool HandleTouchGesture()
    {
        // A press-and-hold with a single finger stands in for a right-click, opening the
        // teleport menu. It is only meaningful while exactly one finger is down and no menu
        // is already showing; a second finger (pinch) or an open menu cancels the tracker.
        var singleTouch = Input.TouchLocations.Count == 1;
        if (!singleTouch || _activeMenu is not null)
        {
            _touchLongPress.Reset();
        }

        var touches = new List<TouchMapPoint>(2);
        for (var index = 0; index < Input.TouchLocations.Count; index++)
        {
            var touch = Input.TouchLocations[index];
            var localEngine = _surface.ScreenToWidget(touch.Position);
            var local = new NVector2(localEngine.X, localEngine.Y);
            var phase = touch.State switch
            {
                TouchLocationState.Pressed => MiniMapTouchPhase.Pressed,
                TouchLocationState.Released => MiniMapTouchPhase.Released,
                _ => MiniMapTouchPhase.Moved,
            };
            var deathTap = _deathTouchTap.Update(
                touch.Id,
                local,
                phase,
                _surface.HitLastDeath(local) is not null || _surface.HitPreviousDeath(local) is not null,
                dragThreshold: 12f * MathF.Max(1f, GlobalScale));
            if (deathTap.Activate
                && (_surface.HitLastDeath(local) ?? _surface.HitPreviousDeath(local)) is { } death)
            {
                _touchGesture.Reset();
                ResetToWorld(new NVector2(death.Position.X, death.Position.Z));
                Input.Clear();
                return true;
            }

            if (singleTouch && _activeMenu is null && _surface.ContainsLocalPoint(local))
            {
                var longPress = _touchLongPress.Update(
                    touch.Id,
                    local,
                    phase,
                    Time.FrameStartTime,
                    holdDuration: 0.5d,
                    dragThreshold: 12f * MathF.Max(1f, GlobalScale));
                if (longPress.Activate)
                {
                    _touchGesture.Reset();
                    // The same finger is also being tracked as a death-marker tap; clear it so the
                    // upcoming release doesn't fire a "locate death" that dismisses the menu we just
                    // opened by long-pressing that marker.
                    _deathTouchTap.Reset();
                    OpenTeleportMenuAt(local, touch.Position);
                    Input.Clear();
                    return true;
                }
            }

            if (touch.State == TouchLocationState.Released)
            {
                continue;
            }

            if (_touchGesture.Mode != TouchMapGestureMode.Idle
                || _surface.ContainsLocalPoint(local))
            {
                touches.Add(new TouchMapPoint(touch.Id, local));
            }
        }

        var update = _touchGesture.Update(
            touches,
            _surface.Transform,
            minimumBlocksPerPixel: 0.25f,
            maximumBlocksPerPixel: 32f);
        ApplyTransformCommand(update.Command);
        return update.Consumed;
    }

    private void LocatePlayer()
    {
        var position = _playerPose().Position;
        _surface.Transform = _followState.Locate(
            _surface.Transform,
            new NVector2(position.X, position.Z));
        HideContextMenu();
        RefreshScaleText();
    }

    private void RefreshTopInformation()
    {
        var position = _playerPose().Position;
        var coordinate = ((int)position.X, (int)position.Y, (int)position.Z);
        var coordinateChanged = coordinate != _lastTopCoordinate || _topCoordinateText.Length == 0;
        if (coordinateChanged)
        {
            _lastTopCoordinate = coordinate;
            _topCoordinateText = TravelMapRenderModel.FormatCoordinates(position);
        }

        var minute = GameTimeFormatter.GetDisplayedMinute(_gameTime());
        var minuteChanged = minute != _lastTopMinute || _topTimeText.Length == 0;
        if (minuteChanged)
        {
            _lastTopMinute = minute;
            _topTimeText = GameTimeFormatter.FormatMinute(minute);
        }

        var visibilityChanged = _lastShowCoordinates != _settings.ShowCoordinates
            || _lastShowGameTime != _settings.ShowGameTime;
        if (coordinateChanged || minuteChanged || visibilityChanged)
        {
            _lastShowCoordinates = _settings.ShowCoordinates;
            _lastShowGameTime = _settings.ShowGameTime;
            var coordinateText = _settings.ShowCoordinates ? _topCoordinateText : string.Empty;
            var timeText = _settings.ShowGameTime ? _topTimeText : string.Empty;
            _coordinateLabel.Text = coordinateText;
            _timeLabel.Text = timeText;
        }

        _coordinateLabel.IsVisible = _coordinateLabel.Text.Length > 0;
        _timeLabel.IsVisible = _timeLabel.Text.Length > 0;
        _mapInformationHost.IsVisible = _coordinateLabel.IsVisible || _timeLabel.IsVisible;
    }

    private void OpenTeleportMenuAt(NVector2 local, Vector2 screenPointer)
    {
        var world = _surface.Transform.ScreenToWorld(local);
        var command = _controller.HandleRightClick(
            world,
            _surface.IsExplored(world),
            _surface.HitWaypoint(local),
            _surface.HitLastDeath(local) is not null,
            _surface.HitPreviousDeath(local) is not null);
        if (_mapViewState.Mode == MapViewMode.Cave && command.ContextMenu is { } caveMenu)
        {
            command = command with
            {
                ContextMenu = caveMenu with { TargetY = _mapViewState.CaveY },
            };
        }

        HandleContextCommand(command, screenPointer);
    }

    private void HandleContextCommand(TravelMapUiCommand command, Vector2 screenPointer)
    {
        if (command.Kind == TravelMapUiCommandKind.ShowUnexploredMessage)
        {
            HideContextMenu();
            Notify(
                TravelMapText.Get("areaUnexplored", "该区域尚未探索"),
                TravelMapNoticeKind.Information);
            return;
        }

        if (command.ContextMenu is not { } menu)
        {
            return;
        }

        _activeMenu = menu;
        _contextActions.Children.Clear();
        _contextButtons.Clear();
        foreach (var action in menu.Actions)
        {
            var button = CreateButton(ActionText(action), 210f);
            button.Size = new Vector2(210f, 40f);
            _contextActions.Children.Add(button);
            _contextButtons.Add((button, action));
        }

        var cardHeight = 54f + (menu.Actions.Count * 46f);
        _contextCard.Size = new Vector2(240f, cardHeight);
        if (_contextCard.Children[0] is RectangleWidget background)
        {
            background.Size = _contextCard.Size;
        }

        var localPointer = ScreenToWidget(screenPointer);
        var x = Math.Clamp(localPointer.X + 12f, 8f, MathF.Max(8f, ActualSize.X - _contextCard.Size.X - 8f));
        var y = Math.Clamp(localPointer.Y + 12f, 54f, MathF.Max(54f, ActualSize.Y - _contextCard.Size.Y - 8f));
        SetWidgetPosition(_contextCard, new Vector2(x, y));
        _contextCard.IsVisible = true;
    }

    private async Task ExecuteActionAsync(
        TravelMapContextAction action,
        TravelMapContextMenu menu,
        CancellationToken cancellationToken)
    {
        HideContextMenu();
        try
        {
            var result = await _actionHandler(action, menu, cancellationToken).ConfigureAwait(false);
            if (result == TravelMapActionStatus.Unavailable)
            {
                Notify(
                    TravelMapText.Get(
                        "travelActionUnavailable",
                        "当前服务器或游戏模式无法执行该旅行操作"),
                    TravelMapNoticeKind.Information);
            }
            else if (result == TravelMapActionStatus.Failed)
            {
                Notify(
                    TravelMapText.Get("travelActionFailed", "旅行操作未完成"),
                    TravelMapNoticeKind.Failure);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void RefreshScaleText()
    {
        var scale = _surface.Transform.BlocksPerPixel;
        if (scale == _lastScale)
        {
            return;
        }

        _lastScale = scale;
        _scaleLabel.Text = TravelMapText.Format(
            "scaleFormat",
            "比例  1 px : {0:0.00} 方块",
            scale);
    }

    private void RefreshMapModeControls()
    {
        var cave = _mapViewState.Mode == MapViewMode.Cave;
        _surfaceModeButton.Text = cave
            ? TravelMapText.Get("surfaceMode", "地表")
            : "● " + TravelMapText.Get("surfaceMode", "地表");
        _caveModeButton.Text = cave
            ? "● " + TravelMapText.Get("caveMode", "洞穴")
            : TravelMapText.Get("caveMode", "洞穴");
        _caveYDownButton.IsVisible = cave;
        _caveYUpButton.IsVisible = cave;
        _caveFollowYButton.IsVisible = cave;
        _caveYLabel.IsVisible = cave;
        _caveYLabel.Text = $"Y: {_mapViewState.CaveY}";
        _caveFollowYButton.Text = _mapViewState.FollowsPlayerY
            ? TravelMapText.Get("followingPlayerY", "跟随Y轴中")
            : TravelMapText.Get("followPlayerY", "跟随玩家Y轴");
    }

    private void ShowCaveYInput()
    {
        var title = TravelMapText.Get("enterCaveY", "输入洞穴高度")
            + $" ({CaveLayer.MinimumY}-{CaveLayer.MaximumY})";
        DialogsManager.ShowDialog(
            ParentWidget as ContainerWidget,
            new TextBoxDialog(
                title,
                _mapViewState.CaveY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maximumLength: 3,
                result =>
                {
                    if (result is null)
                    {
                        return;
                    }

                    if (!int.TryParse(
                            result.Trim(),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var y)
                        || y < CaveLayer.MinimumY
                        || y > CaveLayer.MaximumY)
                    {
                        Notify(
                            TravelMapText.Get(
                                "invalidCaveY",
                                "请输入 1 到 254 之间的整数"),
                            TravelMapNoticeKind.Information);
                        return;
                    }

                    _mapViewState.SetCaveY(y);
                    RefreshMapModeControls();
                }));
    }

    private void HideContextMenu()
    {
        _activeMenu = null;
        _contextCard.IsVisible = false;
    }

    private void CloseSettings()
    {
        _settingsWidget.RequestPersist();
        _settingsWidget.IsVisible = false;
        _lastDragPosition = null;
    }

    private void PersistScale()
    {
        if (!_scaleSavePending)
        {
            return;
        }

        _scaleSavePending = false;
        _persistenceRunner.TryRun(PersistScaleAsync);
    }

    private async Task PersistScaleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notify(
                TravelMapText.Get(
                    "largeMapZoomSaveFailed",
                    "大地图比例未能保存；本次会话仍会保留更改。"),
                TravelMapNoticeKind.Failure);
        }
    }

    private void Notify(string message, TravelMapNoticeKind kind) =>
        _notify(new TravelMapNotice(message, kind));

    private static BevelledButtonWidget CreateButton(string text, float width) => new()
    {
        Text = text,
        Size = new Vector2(width, 42f),
        Color = SnowText,
        CenterColor = Basalt,
        BevelColor = Moss,
        BevelSize = 1f,
    };

    private static string ActionText(TravelMapContextAction action) => action switch
    {
        TravelMapContextAction.TeleportNearby => TravelMapText.Get("teleportHere", "传送到这里"),
        TravelMapContextAction.AddWaypoint => TravelMapText.Get("saveMapPoint", "保存该位置"),
        TravelMapContextAction.AddPlayerWaypoint => TravelMapText.Get(
            "saveCurrentPosition",
            "保存当前位置"),
        TravelMapContextAction.TeleportToWaypoint => TravelMapText.Get("teleportToWaypoint", "传送到此坐标点"),
        TravelMapContextAction.RenameWaypoint => TravelMapText.Get("rename", "重命名"),
        TravelMapContextAction.DeleteWaypoint => TravelMapText.Get("delete", "删除"),
        TravelMapContextAction.Cancel => TravelMapText.Get("cancel", "取消"),
        TravelMapContextAction.TeleportToLastDeath => TravelMapText.Get(
            "teleportToLastDeath",
            "传送至上次死亡地点"),
        TravelMapContextAction.DeleteLastDeath => TravelMapText.Get(
            "deleteDeathMarker",
            "删除死亡标记"),
        TravelMapContextAction.TeleportToPreviousDeath => TravelMapText.Get(
            "teleportToPreviousDeath",
            "传送到此前死亡地点"),
        TravelMapContextAction.DeletePreviousDeath => TravelMapText.Get(
            "deleteDeathMarker",
            "删除死亡标记"),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
