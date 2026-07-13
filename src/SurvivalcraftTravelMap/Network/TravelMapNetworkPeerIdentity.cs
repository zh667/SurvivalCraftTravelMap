using Game.NetWork;
using Game;

namespace SurvivalcraftTravelMap.Network;

internal static class TravelMapNetworkPeerIdentity
{
    internal static string ForClient(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return $"{client.ID}:{client.PlayerGuid:N}:{client.TokenId:N}";
    }
}

internal readonly record struct TravelMapPeerState(
    object? Client,
    object? OwnerClient,
    object? PlayerData,
    object? Player,
    Guid ClientPlayerGuid,
    Guid PlayerGuid,
    Guid TokenId,
    byte ClientId,
    bool IsConnected);

internal sealed class TravelMapBoundPeer
{
    private readonly Func<TravelMapPeerState> _currentState;
    private readonly TravelMapPeerState _boundState;
    private readonly object _expectedPlayer;

    private TravelMapBoundPeer(
        Func<TravelMapPeerState> currentState,
        TravelMapPeerState boundState,
        object expectedPlayer,
        CancellationToken operationToken)
    {
        _currentState = currentState;
        _boundState = boundState;
        _expectedPlayer = expectedPlayer;
        OperationToken = operationToken;
        Identity = $"{boundState.ClientId}:{boundState.ClientPlayerGuid:N}:{boundState.TokenId:N}";
    }

    internal string Identity { get; }

    internal CancellationToken OperationToken { get; }

    internal bool IsCurrent
    {
        get
        {
            if (OperationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                return Matches(_currentState(), _boundState, _expectedPlayer);
            }
            catch
            {
                return false;
            }
        }
    }

    internal static TravelMapBoundPeer? TryCreate(
        Func<TravelMapPeerState> currentState,
        object expectedPlayer,
        CancellationToken operationToken)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(expectedPlayer);
        TravelMapPeerState initial;
        try
        {
            initial = currentState();
        }
        catch
        {
            return null;
        }

        return operationToken.IsCancellationRequested || !IsInitiallyValid(initial, expectedPlayer)
            ? null
            : new TravelMapBoundPeer(currentState, initial, expectedPlayer, operationToken);
    }

    internal static TravelMapBoundPeer? TryCreate(
        Client source,
        ComponentPlayer player,
        CancellationToken operationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(player);
        return TryCreate(() => Capture(source, player), player, operationToken);
    }

    private static TravelMapPeerState Capture(Client source, ComponentPlayer player)
    {
        var playerData = source.PlayerData;
        return new TravelMapPeerState(
            source,
            playerData?.Client,
            playerData,
            playerData?.ComponentPlayer,
            source.PlayerGuid,
            player.PlayerGuid,
            source.TokenId,
            source.ID,
            source.IsConnected);
    }

    private static bool IsInitiallyValid(TravelMapPeerState state, object expectedPlayer) =>
        state.Client is not null
        && state.PlayerData is not null
        && state.Player is not null
        && state.IsConnected
        && ReferenceEquals(state.OwnerClient, state.Client)
        && ReferenceEquals(state.Player, expectedPlayer)
        && state.ClientPlayerGuid == state.PlayerGuid;

    private static bool Matches(
        TravelMapPeerState current,
        TravelMapPeerState bound,
        object expectedPlayer) =>
        IsInitiallyValid(current, expectedPlayer)
        && ReferenceEquals(bound.Player, expectedPlayer)
        && ReferenceEquals(current.Client, bound.Client)
        && ReferenceEquals(current.OwnerClient, bound.Client)
        && ReferenceEquals(current.PlayerData, bound.PlayerData)
        && ReferenceEquals(current.Player, bound.Player)
        && current.ClientPlayerGuid == bound.ClientPlayerGuid
        && current.PlayerGuid == bound.PlayerGuid
        && current.TokenId == bound.TokenId
        && current.ClientId == bound.ClientId;
}

internal static class CoordinateTeleportBoundOperation
{
    internal static async Task<CoordinateTeleportMessage> ExecuteAsync(
        TravelMapBoundPeer binding,
        CoordinateTeleportServerSession session,
        CoordinateTeleportMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        if (!binding.IsCurrent)
        {
            return CoordinateTeleportMessage.Result(
                message.RequestId,
                CoordinateTeleportResultCode.Rejected);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            binding.OperationToken);
        var response = await session.HandleAsync(
            binding.Identity,
            message,
            linked.Token).ConfigureAwait(false);
        return binding.IsCurrent
            ? response
            : CoordinateTeleportMessage.Result(
                message.RequestId,
                CoordinateTeleportResultCode.Disconnected);
    }
}

public static class CoordinateTeleportResultText
{
    public static string For(CoordinateTeleportResultCode result) => result switch
    {
        CoordinateTeleportResultCode.Rejected => "服务器拒绝了地图传送请求",
        CoordinateTeleportResultCode.Unsupported => "服务器不支持地图传送",
        CoordinateTeleportResultCode.Disabled => "服务器已关闭此类地图传送",
        CoordinateTeleportResultCode.TimedOut => CoordinateTeleportClientSession.UnsupportedOrTimeoutMessage,
        CoordinateTeleportResultCode.NoSafePosition => "目标附近没有安全落点",
        CoordinateTeleportResultCode.OutOfWorld => "目标坐标超出世界范围",
        CoordinateTeleportResultCode.RolledBack => "落点复查失败，已回到原位置",
        CoordinateTeleportResultCode.Malformed => "服务器拒绝了无效请求",
        CoordinateTeleportResultCode.Duplicate => "重复的地图传送请求已被拒绝",
        CoordinateTeleportResultCode.Disconnected => "连接已断开，地图传送已取消",
        CoordinateTeleportResultCode.InternalError => "服务器执行地图传送时出错",
        CoordinateTeleportResultCode.Success => "传送完成",
        _ => "地图传送未完成",
    };
}
