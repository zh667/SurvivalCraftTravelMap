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
    private readonly Action<TravelMapNotice> _notify;
    private readonly TravelMapContextActionHandler _actionHandler;
    private readonly TrackedUiActionRunner _actionRunner;
    private readonly TrackedUiActionRunner _persistenceRunner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RectangleWidget _background;
    private readonly CanvasWidget _mapHost;
    private readonly MapSurfaceWidget _surface;
    private readonly CanvasWidget _topBar;
    private readonly LabelWidget _scaleLabel;
    private readonly BevelledButtonWidget _settingsButton;
    private readonly BevelledButtonWidget _closeButton;
    private readonly TravelMapSettingsWidget _settingsWidget;
    private readonly CanvasWidget _contextCard;
    private readonly StackPanelWidget _contextActions;
    private readonly TravelMapNoticeController _noticeController =
        new TravelMapNoticeController(TimeSpan.FromSeconds(2.5));
    private readonly CanvasWidget _noticeHost;
    private readonly RectangleWidget _noticeBackground;
    private readonly LabelWidget _noticeLabel;
    private readonly List<(BevelledButtonWidget Button, TravelMapContextAction Action)> _contextButtons = [];
    private TravelMapContextMenu? _activeMenu;
    private NVector2? _lastDragPosition;
    private float _lastScale = float.NaN;
    private bool _scaleSavePending;
    private double _scaleSaveTime;

    public TravelMapDialog(
        IExploredMapPixelSource pixelSource,
        TravelMapSettings settings,
        TravelMapSettingsStore settingsStore,
        Func<PlayerMapPose> playerPose,
        Func<IReadOnlyList<Waypoint>> waypoints,
        Func<float> brightness,
        TravelMapContextActionHandler actionHandler,
        Action<TravelMapNotice> notify)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _playerPose = playerPose ?? throw new ArgumentNullException(nameof(playerPose));
        _actionHandler = actionHandler ?? throw new ArgumentNullException(nameof(actionHandler));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _actionRunner = new TrackedUiActionRunner(
            _ => Notify("地图操作未能完成", TravelMapNoticeKind.Failure));
        _persistenceRunner = new TrackedUiActionRunner(
            _ => Notify("大地图比例未能保存，本次会话将保留当前值", TravelMapNoticeKind.Failure));

        _background = new RectangleWidget
        {
            FillColor = Basalt,
            OutlineColor = Color.Transparent,
        };
        Children.Add(_background);

        _mapHost = new CanvasWidget { ClampToBounds = true };
        Children.Add(_mapHost);
        _surface = new MapSurfaceWidget(pixelSource, settings, playerPose, waypoints, brightness)
        {
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
        _mapHost.Children.Add(_surface);

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
            Text = "荒野测绘仪  /  旅行图",
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

        _settingsButton = CreateButton("设置", 92f);
        _closeButton = CreateButton("关闭", 92f);
        _topBar.Children.Add(_settingsButton);
        _topBar.Children.Add(_closeButton);

        _settingsWidget = new TravelMapSettingsWidget(
            settings,
            settingsStore,
            message => Notify(message, TravelMapNoticeKind.Failure),
            CloseSettings)
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
            Text = "测量点操作",
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
        _topBar.Size = new Vector2(ActualSize.X, 48f);
        _topBar.SetWidgetPosition(_settingsButton, new Vector2(MathF.Max(0f, ActualSize.X - 204f), 3f));
        _topBar.SetWidgetPosition(_closeButton, new Vector2(MathF.Max(0f, ActualSize.X - 104f), 3f));
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
        _surface.Transform = _controller.CenterLargeMap(
            new NVector2(position.X, position.Z),
            new NVector2(MathF.Max(1f, _surface.ActualSize.X), MathF.Max(1f, _surface.ActualSize.Y)),
            _settings.LargeMapBlocksPerPixel);
        HideContextMenu();
        _settingsWidget.IsVisible = false;
        _noticeController.Clear();
        _noticeHost.IsVisible = false;
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

        if (_settingsButton.IsClicked)
        {
            _settingsWidget.IsVisible = !_settingsWidget.IsVisible;
            HideContextMenu();
        }

        if (_settingsWidget.IsVisible)
        {
            _lastDragPosition = null;
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

        var pointer = Input.MousePosition;
        if (!pointer.HasValue)
        {
            _lastDragPosition = null;
            return;
        }

        var localEngine = _surface.ScreenToWidget(pointer.Value);
        var local = new NVector2(localEngine.X, localEngine.Y);
        var hovered = local.X >= 0f
            && local.Y >= 0f
            && local.X <= _surface.ActualSize.X
            && local.Y <= _surface.ActualSize.Y;
        _surface.LabelPointer = hovered ? local : null;

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
            var world = _surface.Transform.ScreenToWorld(local);
            var command = _controller.HandleRightClick(
                world,
                _surface.IsExplored(world),
                _surface.HitWaypoint(local));
            HandleContextCommand(command, pointer.Value);
        }

        foreach (var item in _contextButtons)
        {
            if (item.Button.IsClicked)
            {
                if (item.Action == TravelMapContextAction.Cancel)
                {
                    HideContextMenu();
                }
                else if (_activeMenu is not null)
                {
                    var menu = _activeMenu;
                    if (!_actionRunner.TryRun(token => ExecuteActionAsync(item.Action, menu, token)))
                    {
                        Notify("另一项地图操作仍在执行", TravelMapNoticeKind.Information);
                    }
                }

                break;
            }
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

    private void HandleContextCommand(TravelMapUiCommand command, Vector2 screenPointer)
    {
        if (command.Kind == TravelMapUiCommandKind.ShowUnexploredMessage)
        {
            HideContextMenu();
            Notify("该区域尚未探索", TravelMapNoticeKind.Information);
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
                Notify("当前服务器或游戏模式无法执行该旅行操作", TravelMapNoticeKind.Information);
            }
            else if (result == TravelMapActionStatus.Failed)
            {
                Notify("旅行操作未完成", TravelMapNoticeKind.Failure);
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
        _scaleLabel.Text = $"比例  1 px : {scale:0.00} blocks";
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
            Notify("大地图比例未能保存；本次会话仍会保留更改。", TravelMapNoticeKind.Failure);
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
        TravelMapContextAction.TeleportNearby => "传送到这里",
        TravelMapContextAction.AddWaypoint => "保存当前位置",
        TravelMapContextAction.TeleportToWaypoint => "传送到此坐标点",
        TravelMapContextAction.RenameWaypoint => "重命名",
        TravelMapContextAction.DeleteWaypoint => "删除",
        TravelMapContextAction.Cancel => "取消",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
