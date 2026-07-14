namespace SurvivalcraftTravelMap.UI;

public enum TravelMapNoticeKind
{
    Information,
    Success,
    Failure,
}

public readonly record struct TravelMapNotice(string Text, TravelMapNoticeKind Kind);

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
