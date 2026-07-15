using Engine;
using Game;
using SurvivalcraftTravelMap.Network;

namespace SurvivalcraftTravelMap.UI;

public static class TeleportPanelModel
{
    public const int PlayersPerPage = 4;

    public static IReadOnlyList<LegacyGpsPlayerData> GetPage(
        IReadOnlyList<LegacyGpsPlayerData> players,
        int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var start = (long)pageIndex * PlayersPerPage;
        if (start >= players.Count)
        {
            return [];
        }

        return players
            .Skip((int)start)
            .Take(PlayersPerPage)
            .ToArray();
    }
}

public sealed record LegacyTeleportPlayer(Guid Id, string Name);

public sealed class TeleportPanelWidget : Dialog
{
    private readonly Func<IReadOnlyList<LegacyTeleportPlayer>> _players;
    private readonly Action<Guid> _requestTeleport;
    private readonly LabelWidget _pageLabel;
    private readonly BevelledButtonWidget _previous;
    private readonly BevelledButtonWidget _next;
    private readonly BevelledButtonWidget _close;
    private readonly BevelledButtonWidget[] _playerButtons = new BevelledButtonWidget[TeleportPanelModel.PlayersPerPage];
    private IReadOnlyList<LegacyTeleportPlayer> _visiblePlayers = [];
    private int _pageIndex;

    public TeleportPanelWidget(
        Func<IReadOnlyList<LegacyTeleportPlayer>> players,
        Action<Guid> requestTeleport)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _requestTeleport = requestTeleport ?? throw new ArgumentNullException(nameof(requestTeleport));
        Size = new Vector2(520f, 430f);
        Children.Add(new RectangleWidget
        {
            Size = Size,
            FillColor = new Color(0x1B, 0x26, 0x28, 0xFA),
            OutlineColor = new Color(0x6F, 0x8A, 0x3B),
            OutlineThickness = 2f,
        });
        var title = new LabelWidget
        {
            Text = TravelMapText.Get("invitePanelTitle", "玩家邀请传送"),
            Size = new Vector2(360f, 48f),
            FontScale = 0.95f,
            Color = new Color(0xE8, 0xEC, 0xE7),
        };
        Children.Add(title);
        SetWidgetPosition(title, new Vector2(20f, 14f));

        for (var index = 0; index < _playerButtons.Length; index++)
        {
            var button = CreateButton(string.Empty, 470f);
            _playerButtons[index] = button;
            Children.Add(button);
            SetWidgetPosition(button, new Vector2(24f, 70f + (index * 62f)));
        }

        _previous = CreateButton(TravelMapText.Get("previousPage", "上一页"), 120f);
        _next = CreateButton(TravelMapText.Get("nextPage", "下一页"), 120f);
        _close = CreateButton(TravelMapText.Get("close", "关闭"), 100f);
        _pageLabel = new LabelWidget
        {
            Size = new Vector2(130f, 42f),
            Color = new Color(0x74, 0xC9, 0xC8),
            FontScale = 0.82f,
        };
        Children.Add(_previous);
        Children.Add(_next);
        Children.Add(_close);
        Children.Add(_pageLabel);
        SetWidgetPosition(_previous, new Vector2(24f, 338f));
        SetWidgetPosition(_pageLabel, new Vector2(164f, 342f));
        SetWidgetPosition(_next, new Vector2(286f, 338f));
        SetWidgetPosition(_close, new Vector2(414f, 338f));
        Refresh();
    }

    public override void Update()
    {
        if (Input.Cancel || _close.IsClicked)
        {
            DialogsManager.HideDialog(this);
            return;
        }

        if (_previous.IsClicked && _pageIndex > 0)
        {
            _pageIndex--;
            Refresh();
        }

        var players = _players();
        var pageCount = Math.Max(1, (players.Count + TeleportPanelModel.PlayersPerPage - 1) / TeleportPanelModel.PlayersPerPage);
        if (_next.IsClicked && _pageIndex + 1 < pageCount)
        {
            _pageIndex++;
            Refresh();
        }

        for (var index = 0; index < _visiblePlayers.Count; index++)
        {
            if (_playerButtons[index].IsClicked)
            {
                _requestTeleport(_visiblePlayers[index].Id);
                DialogsManager.HideDialog(this);
                return;
            }
        }
    }

    public void Refresh()
    {
        var players = _players();
        var pageCount = Math.Max(1, (players.Count + TeleportPanelModel.PlayersPerPage - 1) / TeleportPanelModel.PlayersPerPage);
        _pageIndex = Math.Clamp(_pageIndex, 0, pageCount - 1);
        var start = _pageIndex * TeleportPanelModel.PlayersPerPage;
        _visiblePlayers = players.Skip(start).Take(TeleportPanelModel.PlayersPerPage).ToArray();
        for (var index = 0; index < _playerButtons.Length; index++)
        {
            var visible = index < _visiblePlayers.Count;
            _playerButtons[index].IsVisible = visible;
            _playerButtons[index].Text = visible
                ? TravelMapText.Format(
                    "invitePlayerFormat",
                    "邀请传送到  {0}",
                    _visiblePlayers[index].Name)
                : string.Empty;
        }

        _pageLabel.Text = $"{_pageIndex + 1} / {pageCount}";
        _previous.IsEnabled = _pageIndex > 0;
        _next.IsEnabled = _pageIndex + 1 < pageCount;
    }

    private static BevelledButtonWidget CreateButton(string text, float width) => new()
    {
        Text = text,
        Size = new Vector2(width, 48f),
        Color = new Color(0xE8, 0xEC, 0xE7),
        CenterColor = new Color(0x1B, 0x26, 0x28),
        BevelColor = new Color(0x6F, 0x8A, 0x3B),
        BevelSize = 1f,
    };
}
