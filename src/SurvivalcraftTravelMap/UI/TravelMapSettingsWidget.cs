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
    private readonly Action<string> _notify;
    private readonly SliderWidget _miniMapZoom;
    private readonly SliderWidget _largeMapZoom;
    private readonly List<BevelledButtonWidget> _sizeButtons = [];

    public TravelMapSettingsWidget(
        TravelMapSettings settings,
        TravelMapSettingsStore store,
        Action<string> notify)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        Size = new Vector2(420f, 430f);
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
            Text = "测绘仪设置",
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
        Children.Add(settingsStack);
        SetWidgetPosition(settingsStack, new Vector2(20f, 58f));

        settingsStack.Children.Add(CreateToggle(
            "显示右上角小地图",
            settings.IsMiniMapVisible,
            value => settings.IsMiniMapVisible = value));
        settingsStack.Children.Add(CreateToggle(
            "显示 X / Y / Z 坐标",
            settings.ShowCoordinates,
            value => settings.ShowCoordinates = value));
        settingsStack.Children.Add(CreateToggle(
            "启用日夜地形明暗",
            settings.UseDayNightTint,
            value => settings.UseDayNightTint = value));
        settingsStack.Children.Add(CreateToggle(
            "接受玩家传送邀请",
            settings.AcceptTeleportInvitations,
            value => settings.AcceptTeleportInvitations = value));

        _miniMapZoom = CreateSlider(0.5f, 8f, settings.MiniMapBlocksPerPixel);
        _largeMapZoom = CreateSlider(0.25f, 32f, settings.LargeMapBlocksPerPixel);
        settingsStack.Children.Add(CreateSliderRow("小地图 方块/像素", _miniMapZoom));
        settingsStack.Children.Add(CreateSliderRow("大地图 方块/像素", _largeMapZoom));

        var sizeLabel = new LabelWidget
        {
            Text = "小地图尺寸",
            Color = SnowText,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            Size = new Vector2(380f, 30f),
        };
        Children.Add(sizeLabel);
        SetWidgetPosition(sizeLabel, new Vector2(20f, 335f));

        var sizeStack = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(2f),
        };
        Children.Add(sizeStack);
        SetWidgetPosition(sizeStack, new Vector2(20f, 368f));
        foreach (var size in TravelMapSettings.SupportedMiniMapSizes)
        {
            var button = new BevelledButtonWidget
            {
                Text = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Size = new Vector2(72f, 42f),
                Color = SnowText,
                CenterColor = size == settings.MiniMapSize ? Moss : Basalt,
            };
            button.Tag = size;
            _sizeButtons.Add(button);
            sizeStack.Children.Add(button);
        }
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

        if (_miniMapZoom.SlidingCompleted || _largeMapZoom.SlidingCompleted)
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
    }

    private CheckboxWidget CreateToggle(string text, bool value, Action<bool> assign)
    {
        var checkbox = new CheckboxWidget
        {
            Text = text,
            IsChecked = value,
            IsAutoCheckingEnabled = true,
            Size = new Vector2(360f, 38f),
            Color = SnowText,
        };
        checkbox.CheckStatusChanged += isChecked =>
        {
            assign(isChecked);
            Persist();
        };
        return checkbox;
    }

    private static SliderWidget CreateSlider(float minimum, float maximum, float value) => new()
    {
        MinValue = minimum,
        MaxValue = maximum,
        Granularity = 0f,
        Value = value,
        LayoutDirection = LayoutDirection.Horizontal,
        Size = new Vector2(200f, 36f),
        IsLabelVisible = true,
        LabelWidth = 60f,
        TextColor = SnowText,
        Text = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static CanvasWidget CreateSliderRow(string labelText, SliderWidget slider)
    {
        var row = new CanvasWidget { Size = new Vector2(360f, 40f) };
        var label = new LabelWidget
        {
            Text = labelText,
            Color = SnowText,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            Size = new Vector2(160f, 36f),
        };
        row.Children.Add(label);
        row.Children.Add(slider);
        row.SetWidgetPosition(slider, new Vector2(160f, 0f));
        return row;
    }

    private void SetMiniMapSize(int size)
    {
        _settings.MiniMapSize = size;
        foreach (var button in _sizeButtons)
        {
            if (button.Tag is int buttonSize)
            {
                button.CenterColor = buttonSize == size ? Moss : Basalt;
            }
        }

        Persist();
    }

    private void Persist() => _ = PersistAsync();

    private async Task PersistAsync()
    {
        try
        {
            await _store.SaveAsync(_settings).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _notify("地图设置未能保存；本次会话仍会保留更改。");
        }
    }
}
