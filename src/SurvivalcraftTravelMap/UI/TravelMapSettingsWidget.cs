using Engine;
using Game;
using SurvivalcraftTravelMap.Settings;

namespace SurvivalcraftTravelMap.UI;

public sealed class TravelMapSettingsWidget : CanvasWidget
{
    private static readonly Color Basalt = new(0x1B, 0x26, 0x28, 0xF4);
    private static readonly Color Moss = new(0x6F, 0x8A, 0x3B);
    private static readonly Color SnowText = new(0xE8, 0xEC, 0xE7);

    private readonly TravelMapSettings _settings;
    private readonly TravelMapSettingsStore _store;
    private readonly Action<TravelMapNotice> _notify;
    private readonly Action _requestClose;
    private readonly Action _requestMiniMapPlacement;
    private readonly CoalescingSaveQueue _saveQueue;
    private readonly SliderWidget _miniMapZoom;
    private readonly SliderWidget _largeMapZoom;
    private readonly SliderWidget _creatureMarkerSize;
    private readonly SliderWidget _compassFontScale;
    private readonly BevelledButtonWidget _doneButton;
    private readonly BevelledButtonWidget _placementButton;
    private readonly BevelledButtonWidget _resetButton;
    private readonly List<BevelledButtonWidget> _sizeButtons = [];
    private readonly List<BevelledButtonWidget> _shapeButtons = [];
    private readonly List<BevelledButtonWidget> _heightShadingButtons = [];
    private readonly List<(CheckboxWidget Widget, Func<bool> Read)> _toggleBindings = [];
    private bool _refreshingControls;

    public TravelMapSettingsWidget(
        TravelMapSettings settings,
        TravelMapSettingsStore store,
        Action<TravelMapNotice> notify,
        Action requestClose,
        Action requestMiniMapPlacement)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));
        _requestMiniMapPlacement = requestMiniMapPlacement
            ?? throw new ArgumentNullException(nameof(requestMiniMapPlacement));
        _saveQueue = new CoalescingSaveQueue(
            PersistAsync,
            _ => Notify(
                TravelMapText.Get("settingsSaveFailedSession", "地图设置未能保存，本次会话仍保留当前值"),
                TravelMapNoticeKind.Failure),
            TimeSpan.FromMilliseconds(150));
        Size = new Vector2(420f, 550f);
        HorizontalAlignment = WidgetAlignment.Center;
        VerticalAlignment = WidgetAlignment.Center;

        var background = new RectangleWidget
        {
            Size = Size,
            FillColor = Basalt,
            OutlineColor = Moss,
            OutlineThickness = 2f,
        };
        Children.Add(background);

        var title = new LabelWidget
        {
            Text = TravelMapText.Get("settingsTitle", "旅行地图设置"),
            Color = SnowText,
            FontScale = 1.15f,
            Size = new Vector2(380f, 42f),
            HorizontalAlignment = WidgetAlignment.Center,
            TextAnchor = Engine.Graphics.TextAnchor.HorizontalCenter
                | Engine.Graphics.TextAnchor.VerticalCenter,
        };
        Children.Add(title);
        SetWidgetPosition(title, new Vector2(20f, 10f));

        var settingsStack = new StackPanelWidget
        {
            Direction = LayoutDirection.Vertical,
            Margin = new Vector2(3f),
        };
        var settingsScroll = new ScrollPanelWidget
        {
            Direction = LayoutDirection.Vertical,
            ScrollPosition = 0f,
            ScrollSpeed = 0f,
            DesiredSize = new Vector2(380f, 425f),
        };
        settingsScroll.Children.Add(settingsStack);
        Children.Add(settingsScroll);
        SetWidgetPosition(settingsScroll, new Vector2(20f, 58f));

        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showMiniMap", "显示小地图"),
            () => settings.IsMiniMapVisible,
            value => settings.IsMiniMapVisible = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showCoordinates", "显示 X / Y / Z 坐标"),
            () => settings.ShowCoordinates,
            value => settings.ShowCoordinates = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showGameTime", "显示游戏时间"),
            () => settings.ShowGameTime,
            value => settings.ShowGameTime = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showCreatureMarkers", "显示生物标记"),
            () => settings.ShowCreatureMarkers,
            value => settings.ShowCreatureMarkers = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showLastDeathMarker", "显示上次死亡地点"),
            () => settings.ShowLastDeathMarker,
            value => settings.ShowLastDeathMarker = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("northUp", "固定北方朝上"),
            () => settings.MiniMapOrientation == MiniMapOrientation.NorthUp,
            value => settings.MiniMapOrientation = value
                ? MiniMapOrientation.NorthUp
                : MiniMapOrientation.HeadingUp));
        settingsStack.Children.Add(CreateSectionLabel(
            TravelMapText.Get("compassDirections", "显示罗盘方向")));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showCompassNorth", "  显示北方"),
            () => settings.ShowCompassNorth,
            value => settings.ShowCompassNorth = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("showCompassOtherDirections", "  显示东 / 南 / 西"),
            () => settings.ShowCompassOtherDirections,
            value => settings.ShowCompassOtherDirections = value));
        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("useDayNightTint", "启用日夜地形明暗"),
            () => settings.UseDayNightTint,
            value => settings.UseDayNightTint = value));
        settingsStack.Children.Add(CreateSectionLabel(
            TravelMapText.Get("heightShading", "地形阴影")));
        var heightShadingStack = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(2f),
        };
        settingsStack.Children.Add(heightShadingStack);
        foreach (var style in Enum.GetValues<HeightShadingStyle>())
        {
            var button = new BevelledButtonWidget
            {
                Text = TravelMapText.HeightShading(style),
                Size = new Vector2(86f, 38f),
                Color = SnowText,
                CenterColor = style == settings.HeightShadingStyle ? Moss : Basalt,
                Tag = style,
            };
            _heightShadingButtons.Add(button);
            heightShadingStack.Children.Add(button);
        }

        settingsStack.Children.Add(CreateToggle(
            TravelMapText.Get("acceptTeleportInvitations", "接受玩家传送邀请"),
            () => settings.AcceptTeleportInvitations,
            value => settings.AcceptTeleportInvitations = value));

        _miniMapZoom = CreateSlider(0.5f, 8f, settings.MiniMapBlocksPerPixel);
        _largeMapZoom = CreateSlider(0.25f, 32f, settings.LargeMapBlocksPerPixel);
        _creatureMarkerSize = CreateSlider(3f, 16f, settings.CreatureMarkerSize, granularity: 1f);
        _compassFontScale = CreateSlider(0.5f, 2f, settings.CompassFontScale, granularity: 0.1f);
        settingsStack.Children.Add(CreateSliderRow(
            TravelMapText.Get("miniMapZoom", "小地图 方块/像素"),
            _miniMapZoom));
        settingsStack.Children.Add(CreateSliderRow(
            TravelMapText.Get("largeMapZoom", "大地图 方块/像素"),
            _largeMapZoom));
        settingsStack.Children.Add(CreateSliderRow(
            TravelMapText.Get("creatureMarkerSize", "生物标记大小"),
            _creatureMarkerSize));
        settingsStack.Children.Add(CreateSliderRow(
            TravelMapText.Get("compassFontScale", "罗盘文字大小"),
            _compassFontScale));

        var sizeLabel = new LabelWidget
        {
            Text = TravelMapText.Get("miniMapSize", "小地图尺寸"),
            Color = SnowText,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            Size = new Vector2(380f, 30f),
        };
        settingsStack.Children.Add(sizeLabel);

        var sizeStack = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(2f),
        };
        settingsStack.Children.Add(sizeStack);
        foreach (var size in TravelMapSettings.SupportedMiniMapSizes)
        {
            var button = new BevelledButtonWidget
            {
                Text = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Size = new Vector2(72f, 38f),
                Color = SnowText,
                CenterColor = size == settings.MiniMapSize ? Moss : Basalt,
            };
            button.Tag = size;
            _sizeButtons.Add(button);
            sizeStack.Children.Add(button);
        }

        settingsStack.Children.Add(CreateSectionLabel(
            TravelMapText.Get("mapShape", "地图形状")));
        var shapeStack = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(2f),
        };
        settingsStack.Children.Add(shapeStack);
        foreach (var shape in Enum.GetValues<MapShape>())
        {
            var button = new BevelledButtonWidget
            {
                Text = TravelMapText.MapShape(shape),
                Size = new Vector2(86f, 38f),
                Color = SnowText,
                CenterColor = shape == settings.MiniMapShape ? Moss : Basalt,
                Tag = shape,
            };
            _shapeButtons.Add(button);
            shapeStack.Children.Add(button);
        }

        _placementButton = new BevelledButtonWidget
        {
            Text = TravelMapText.Get("adjustMiniMapPosition", "调整小地图位置"),
            Size = new Vector2(220f, 40f),
            Color = SnowText,
            CenterColor = Moss,
        };
        settingsStack.Children.Add(_placementButton);

        _doneButton = new BevelledButtonWidget
        {
            Text = TravelMapText.Get("done", "完成"),
            Size = new Vector2(120f, 40f),
            Color = SnowText,
            CenterColor = Moss,
        };
        Children.Add(_doneButton);
        SetWidgetPosition(_doneButton, new Vector2(280f, 501f));

        _resetButton = new BevelledButtonWidget
        {
            Text = TravelMapText.Get("resetSettings", "恢复默认设置"),
            Size = new Vector2(180f, 40f),
            Color = SnowText,
            CenterColor = Basalt,
        };
        Children.Add(_resetButton);
        SetWidgetPosition(_resetButton, new Vector2(20f, 501f));
    }

    public override void Update()
    {
        if (_miniMapZoom.IsSliding)
        {
            _settings.MiniMapBlocksPerPixel = _miniMapZoom.Value;
            _miniMapZoom.Text = _settings.MiniMapBlocksPerPixel.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_largeMapZoom.IsSliding)
        {
            _settings.LargeMapBlocksPerPixel = _largeMapZoom.Value;
            _largeMapZoom.Text = _settings.LargeMapBlocksPerPixel.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_creatureMarkerSize.IsSliding)
        {
            _settings.CreatureMarkerSize = _creatureMarkerSize.Value;
            _creatureMarkerSize.Text = _settings.CreatureMarkerSize.ToString(
                "0",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_compassFontScale.IsSliding)
        {
            _settings.CompassFontScale = _compassFontScale.Value;
            _compassFontScale.Text = _settings.CompassFontScale.ToString(
                "0.0",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_miniMapZoom.SlidingCompleted
            || _largeMapZoom.SlidingCompleted
            || _creatureMarkerSize.SlidingCompleted
            || _compassFontScale.SlidingCompleted)
        {
            Persist();
        }

        foreach (var button in _sizeButtons)
        {
            if (button.IsClicked && button.Tag is int size)
            {
                SetMiniMapSize(size);
            }
        }

        foreach (var button in _shapeButtons)
        {
            if (button.IsClicked && button.Tag is MapShape shape)
            {
                SetMapShape(shape);
            }
        }

        foreach (var button in _heightShadingButtons)
        {
            if (button.IsClicked && button.Tag is HeightShadingStyle style)
            {
                SetHeightShadingStyle(style);
            }
        }

        if (_placementButton.IsClicked)
        {
            RequestPersist();
            _requestMiniMapPlacement();
            return;
        }

        if (_resetButton.IsClicked)
        {
            RequestResetConfirmation();
            return;
        }

        if (_doneButton.IsClicked)
        {
            RequestPersist();
            _requestClose();
        }
    }

    public override void Dispose()
    {
        _saveQueue.Dispose();
        base.Dispose();
    }

    internal Task WhenSaveIdleAsync(CancellationToken cancellationToken = default) =>
        _saveQueue.WhenIdleAsync(cancellationToken);

    public void RequestPersist() => _saveQueue.RequestSave();

    private CheckboxWidget CreateToggle(string text, Func<bool> read, Action<bool> assign)
    {
        var checkbox = new CheckboxWidget
        {
            Text = text,
            IsChecked = read(),
            IsAutoCheckingEnabled = true,
            Size = new Vector2(360f, 34f),
            Color = SnowText,
        };
        checkbox.CheckStatusChanged += isChecked =>
        {
            if (_refreshingControls)
            {
                return;
            }

            assign(isChecked);
            Persist();
        };
        _toggleBindings.Add((checkbox, read));
        return checkbox;
    }

    private void RequestResetConfirmation()
    {
        if (_store.IsReadOnly)
        {
            Notify(
                TravelMapText.Get(
                    "resetUnavailableReadOnly",
                    "当前设置文件来自更高版本，处于只读状态，无法恢复默认设置。"),
                TravelMapNoticeKind.Information);
            return;
        }

        var dialog = new MessageDialog(
            TravelMapText.Get("resetSettingsTitle", "恢复默认设置"),
            TravelMapText.Get(
                "resetSettingsMessage",
                "将恢复地图的显示、尺寸、比例、位置和标记设置。探索区域、坐标点及传送邀请选项不会改变。"),
            TravelMapText.Get("resetConfirm", "恢复默认"),
            TravelMapText.Get("cancel", "取消"),
            button =>
            {
                if (button == MessageDialogButton.Button1)
                {
                    ApplyReset();
                }
            });
        DialogsManager.ShowDialog(ParentWidget as ContainerWidget, dialog);
    }

    private void ApplyReset()
    {
        _settings.ResetPresentationToDefaults();
        RefreshControlsFromSettings();
        RequestPersist();
        Notify(
            TravelMapText.Get("resetSuccess", "地图显示设置已恢复默认值"),
            TravelMapNoticeKind.Success);
    }

    private void RefreshControlsFromSettings()
    {
        _refreshingControls = true;
        try
        {
            foreach (var binding in _toggleBindings)
            {
                binding.Widget.IsChecked = binding.Read();
            }

            _miniMapZoom.Value = _settings.MiniMapBlocksPerPixel;
            _largeMapZoom.Value = _settings.LargeMapBlocksPerPixel;
            _creatureMarkerSize.Value = _settings.CreatureMarkerSize;
            _compassFontScale.Value = _settings.CompassFontScale;
            _miniMapZoom.Text = _settings.MiniMapBlocksPerPixel.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            _largeMapZoom.Text = _settings.LargeMapBlocksPerPixel.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            _creatureMarkerSize.Text = _settings.CreatureMarkerSize.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            _compassFontScale.Text = _settings.CompassFontScale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            RefreshSizeButtons();
            RefreshShapeButtons();
            RefreshHeightShadingButtons();
        }
        finally
        {
            _refreshingControls = false;
        }
    }

    private static LabelWidget CreateSectionLabel(string text) => new()
    {
        Text = text,
        Color = Moss,
        FontScale = TravelMapTypography.SecondaryLabelScale,
        Size = new Vector2(360f, 28f),
    };

    private static SliderWidget CreateSlider(
        float minimum,
        float maximum,
        float value,
        float granularity = 0f) => new()
    {
        MinValue = minimum,
        MaxValue = maximum,
        Granularity = granularity,
        Value = value,
        LayoutDirection = LayoutDirection.Horizontal,
        Size = new Vector2(200f, 34f),
        IsLabelVisible = true,
        LabelWidth = 60f,
        TextColor = SnowText,
        Text = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static CanvasWidget CreateSliderRow(string labelText, SliderWidget slider)
    {
        var row = new CanvasWidget { Size = new Vector2(360f, 36f) };
        var label = new LabelWidget
        {
            Text = labelText,
            Color = SnowText,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            Size = new Vector2(160f, 34f),
        };
        row.Children.Add(label);
        row.Children.Add(slider);
        row.SetWidgetPosition(slider, new Vector2(160f, 0f));
        return row;
    }

    private void SetMiniMapSize(int size)
    {
        _settings.MiniMapSize = size;
        RefreshSizeButtons();
        Persist();
    }

    private void RefreshSizeButtons()
    {
        foreach (var button in _sizeButtons)
        {
            if (button.Tag is int buttonSize)
            {
                button.CenterColor = buttonSize == _settings.MiniMapSize ? Moss : Basalt;
            }
        }
    }

    private void SetMapShape(MapShape shape)
    {
        _settings.MiniMapShape = shape;
        RefreshShapeButtons();
        Persist();
    }

    private void RefreshShapeButtons()
    {
        foreach (var button in _shapeButtons)
        {
            if (button.Tag is MapShape buttonShape)
            {
                button.CenterColor = buttonShape == _settings.MiniMapShape ? Moss : Basalt;
            }
        }
    }

    private void SetHeightShadingStyle(HeightShadingStyle style)
    {
        _settings.HeightShadingStyle = style;
        RefreshHeightShadingButtons();
        Persist();
    }

    private void RefreshHeightShadingButtons()
    {
        foreach (var button in _heightShadingButtons)
        {
            if (button.Tag is HeightShadingStyle buttonStyle)
            {
                button.CenterColor = buttonStyle == _settings.HeightShadingStyle ? Moss : Basalt;
            }
        }
    }

    private void Persist() => RequestPersist();

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notify(
                TravelMapText.Get("settingsSaveFailed", "地图设置未能保存；本次会话仍会保留更改。"),
                TravelMapNoticeKind.Failure);
        }
    }

    private void Notify(string text, TravelMapNoticeKind kind) =>
        _notify(new TravelMapNotice(text, kind));
}
