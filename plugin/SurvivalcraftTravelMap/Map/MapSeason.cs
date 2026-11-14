namespace SurvivalcraftTravelMap.Map;

/// <summary>
/// The world's seasonal state as far as map colours care: the season temperature offset the
/// terrain updater applies (0 in summer, down to -24 in winter), and whether chunks re-run the
/// freeze/thaw pass when they load.
/// <para>
/// The game only freezes, thaws and snows on terrain while its chunk is loaded:
/// <c>SubsystemWeather</c> runs <c>FreezeThawAndDepositSnow</c> on every chunk as it initialises
/// (Living environment only, snow forced when weather effects are on). So a place the player left
/// in winter keeps its snow on disk until someone goes back — and the instant they do, it is
/// decided by <c>IsPlaceFrozen(base temperature + season temperature, surface height)</c>. The map
/// applies that same rule to what it recorded, which is exactly what the player would find there.
/// </para>
/// The default value shows terrain as it was recorded, with no prediction.
/// </summary>
public readonly record struct MapSeason(int Temperature, bool PredictsFreezing, bool WeatherEffects)
{
    private static long s_current;

    /// <summary>Season used by every map surface; written once a second by the player component.</summary>
    public static MapSeason Current
    {
        get => Unpack(Volatile.Read(ref s_current));
        set => Volatile.Write(ref s_current, Pack(value));
    }

    private static long Pack(MapSeason season) =>
        ((long)(uint)season.Temperature << 2)
        | (season.PredictsFreezing ? 1L : 0L)
        | (season.WeatherEffects ? 2L : 0L);

    private static MapSeason Unpack(long packed) => new(
        (int)(uint)(packed >> 2),
        (packed & 1) != 0,
        (packed & 2) != 0);
}

/// <summary>What a recorded surface pixel turns into when its place freezes.</summary>
public enum MapSeasonKind : byte
{
    /// <summary>Recorded before the map tracked seasons; re-recorded when the player passes by.</summary>
    None = 0,

    /// <summary>A surface that takes snow when frozen and loses it when thawed.</summary>
    Land = 1,

    /// <summary>Water short of the open sea: ice seen on it melts when the place thaws.</summary>
    Water = 2,

    /// <summary>Recorded with season tracking and unaffected by it (rock, buildings, open water).</summary>
    Fixed = 3,
}

/// <summary>
/// One byte per map pixel: kind (bits 7-6), whether it was frozen when recorded (bit 5), and the
/// highest season temperature at which the place is frozen (bits 4-0, offset by
/// <see cref="LimitOffset"/>). Season temperatures run 0..-24, so any limit at or above 0 means
/// "always frozen" and any limit below -24 means "never".
/// </summary>
public static class MapSeasonCode
{
    public const int MinimumLimit = -26;
    public const int MaximumLimit = 5;
    private const int LimitOffset = -MinimumLimit;

    public static byte Create(MapSeasonKind kind, bool observedFrozen, int limit)
    {
        if (kind is MapSeasonKind.None or MapSeasonKind.Fixed)
        {
            return (byte)((int)kind << 6);
        }

        var clamped = Math.Clamp(limit, MinimumLimit, MaximumLimit) + LimitOffset;
        return (byte)(((int)kind << 6) | (observedFrozen ? 0x20 : 0) | clamped);
    }

    public static MapSeasonKind GetKind(byte code) => (MapSeasonKind)(code >> 6);

    public static bool WasObservedFrozen(byte code) => (code & 0x20) != 0;

    public static int GetLimit(byte code) => (code & 0x1F) - LimitOffset;

    public static MapSeasonSample ToSample(byte code) => GetKind(code) switch
    {
        MapSeasonKind.Land => new MapSeasonSample(
            byte.MaxValue,
            WasObservedFrozen(code) ? byte.MaxValue : (byte)0,
            0,
            0,
            (sbyte)GetLimit(code),
            0),
        MapSeasonKind.Water => new MapSeasonSample(
            0,
            0,
            byte.MaxValue,
            WasObservedFrozen(code) ? byte.MaxValue : (byte)0,
            0,
            (sbyte)GetLimit(code)),
        _ => default,
    };
}

/// <summary>
/// Seasonal description of one map pixel or an averaged region of them: the share (0-255) of
/// snowable land and freezable water, the share of each that was frozen when recorded, and their
/// (mean) freeze limits.
/// </summary>
public readonly record struct MapSeasonSample(
    byte Land,
    byte LandFrozen,
    byte Water,
    byte WaterFrozen,
    sbyte LandLimit,
    sbyte WaterLimit)
{
    public bool IsSeasonal => Land != 0 || Water != 0;
}

public static class SeasonalTerrainColor
{
    public static readonly Rgba32 Snow = new(238, 244, 244, byte.MaxValue);
    public static readonly Rgba32 Ice = new(216, 226, 243, byte.MaxValue);

    /// <summary>
    /// The colour a recorded pixel shows in <paramref name="season"/>: its bare colour, with the
    /// frozen share replaced by snow (land) or ice (water).
    /// </summary>
    public static Rgba32 Resolve(Rgba32 bare, in MapSeasonSample sample, MapSeason season)
    {
        if (!sample.IsSeasonal)
        {
            return bare;
        }

        int land;
        int water;
        if (season.PredictsFreezing)
        {
            // Snow is only deposited on load when weather effects are on; without them a frozen
            // place merely keeps whatever snow it had.
            land = season.Temperature <= sample.LandLimit
                ? (season.WeatherEffects ? sample.Land : sample.LandFrozen)
                : 0;
            // Ice is predicted to melt, never to form where it was not seen: the game freezes water
            // on load in random order with a second 66% pass, and in the isolated runs only about
            // half of the freezable water iced over, so any "it will freeze" guess is a coin flip.
            // Melting is certain (every bit of ice goes once the place thaws).
            water = season.Temperature <= sample.WaterLimit ? sample.WaterFrozen : 0;
        }
        else
        {
            land = sample.LandFrozen;
            water = sample.WaterFrozen;
        }

        if (land == 0 && water == 0)
        {
            return bare;
        }

        var keep = Math.Max(0, byte.MaxValue - land - water);
        return new Rgba32(
            Mix(bare.R, Snow.R, Ice.R, keep, land, water),
            Mix(bare.G, Snow.G, Ice.G, keep, land, water),
            Mix(bare.B, Snow.B, Ice.B, keep, land, water),
            bare.A);
    }

    private static byte Mix(byte bare, byte snow, byte ice, int keep, int land, int water)
    {
        var total = keep + land + water;
        return (byte)(((bare * keep) + (snow * land) + (ice * water) + (total / 2)) / total);
    }
}
