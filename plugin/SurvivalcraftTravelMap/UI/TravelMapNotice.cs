using SurvivalcraftTravelMap.Teleport;

namespace SurvivalcraftTravelMap.UI;

public enum TravelMapNoticeKind
{
    Information,
    Success,
    Failure,
}

public readonly record struct TravelMapNotice(string Text, TravelMapNoticeKind Kind);

public static class TravelMapNoticeFactory
{
    public static TravelMapNotice For(TeleportResult result) => new(
        TextFor(result),
        result == TeleportResult.Success
            ? TravelMapNoticeKind.Success
            : TravelMapNoticeKind.Failure);

    private static string TextFor(TeleportResult result) => result switch
    {
        TeleportResult.Success => TravelMapText.Get("teleportSuccess", "传送完成"),
        TeleportResult.ChunkTimeout => TravelMapText.Get("teleportIncomplete", "地图传送未完成"),
        TeleportResult.NoSafePosition => TravelMapText.Get("noSafePosition", "目标附近没有安全落点"),
        TeleportResult.OutOfWorld => TravelMapText.Get("targetOutOfWorld", "目标坐标超出世界范围"),
        TeleportResult.RolledBack => TravelMapText.Get("teleportRolledBack", "落点复查失败，已回到原位置"),
        TeleportResult.Busy => TravelMapText.Get("teleportBusy", "另一次传送仍在进行，请稍后再试"),
        _ => TravelMapText.Get("teleportIncomplete", "地图传送未完成"),
    };
}

public sealed class TravelMapNoticeController
{
    private readonly double _durationSeconds;
    private double _expiresAt;

    public TravelMapNoticeController(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _durationSeconds = duration.TotalSeconds;
    }

    public TravelMapNotice? Current { get; private set; }

    public void Show(TravelMapNotice notice, double now)
    {
        if (string.IsNullOrWhiteSpace(notice.Text))
        {
            throw new ArgumentException("Notice text is required.", nameof(notice));
        }

        if (!double.IsFinite(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        Current = notice;
        _expiresAt = now + _durationSeconds;
    }

    public bool Update(double now)
    {
        if (!double.IsFinite(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        if (Current.HasValue && now >= _expiresAt)
        {
            Current = null;
        }

        return Current.HasValue;
    }

    public void Clear() => Current = null;
}
