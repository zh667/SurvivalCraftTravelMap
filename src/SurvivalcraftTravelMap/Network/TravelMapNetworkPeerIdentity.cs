using Game.NetWork;

namespace SurvivalcraftTravelMap.Network;

internal static class TravelMapNetworkPeerIdentity
{
    internal static string ForClient(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return $"{client.ID}:{client.PlayerGuid:N}:{client.TokenId:N}";
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
