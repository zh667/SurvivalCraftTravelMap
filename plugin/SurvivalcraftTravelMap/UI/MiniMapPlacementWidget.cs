using Engine;
using Game;

namespace SurvivalcraftTravelMap.UI;

internal sealed class MiniMapPlacementWidget : Dialog
{
    private static readonly Color Basalt = new(0x1B, 0x26, 0x28, 0xF4);
    private static readonly Color Moss = new(0x6F, 0x8A, 0x3B);
    private static readonly Color SnowText = new(0xE8, 0xEC, 0xE7);

    private readonly Action _confirm;
    private readonly Action _cancel;
    private readonly BevelledButtonWidget _confirmButton;
    private readonly BevelledButtonWidget _cancelButton;

    public MiniMapPlacementWidget(Action confirm, Action cancel)
    {
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        Size = new Vector2(320f, 72f);
        Children.Add(new RectangleWidget
        {
            Size = Size,
            FillColor = Basalt,
            OutlineColor = Moss,
            OutlineThickness = 2f,
        });
        var label = new LabelWidget
        {
            Text = TravelMapText.Get("miniMapPlacementHint", "拖动小地图"),
            Color = SnowText,
            FontScale = TravelMapTypography.SecondaryLabelScale,
            Size = new Vector2(140f, 52f),
            TextAnchor = Engine.Graphics.TextAnchor.HorizontalCenter
                | Engine.Graphics.TextAnchor.VerticalCenter,
        };
        Children.Add(label);
        SetWidgetPosition(label, new Vector2(8f, 10f));

        _confirmButton = CreateButton(TravelMapText.Get("confirm", "确认"));
        _cancelButton = CreateButton(TravelMapText.Get("cancel", "取消"));
        Children.Add(_confirmButton);
        Children.Add(_cancelButton);
        SetWidgetPosition(_confirmButton, new Vector2(148f, 14f));
        SetWidgetPosition(_cancelButton, new Vector2(232f, 14f));
    }

    public override void Update()
    {
        if (_confirmButton.IsClicked)
        {
            _confirm();
        }
        else if (_cancelButton.IsClicked)
        {
            _cancel();
        }
    }

    private static BevelledButtonWidget CreateButton(string text) => new()
    {
        Text = text,
        Size = new Vector2(78f, 44f),
        Color = SnowText,
        CenterColor = Moss,
    };
}
