using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Settings;

internal delegate Task TravelMapSettingsAtomicWriter(
    string path,
    Func<Stream, CancellationToken, Task> write,
    CancellationToken cancellationToken);

public enum TravelMapSettingsLoadOutcome
{
    Loaded,
    Created,
    MigratedUnversioned,
    MigratedPreviousSchema,
    MigratedPreviousPath,
    CorruptIsolated,
    UnsupportedFutureSchemaReadOnly,
}

public sealed record TravelMapSettingsLoadResult(
    TravelMapSettings Settings,
    TravelMapSettingsLoadOutcome Outcome,
    bool IsReadOnly);

public sealed class TravelMapSettingsFutureSchemaWarningGate
{
    private int _notified;

    public void NotifyIfNeeded(TravelMapSettingsLoadResult result, Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(notify);
        if (result.IsReadOnly && Interlocked.Exchange(ref _notified, 1) == 0)
        {
            notify("检测到更高版本的地图设置；本次会话只读，原文件不会被覆盖。");
        }
    }
}

public sealed class TravelMapSettingsStore
{
    private const int CurrentSchemaVersion = 2;
    private const string SettingsFileName = "settings.json";
    private const string PreviousSettingsFileName = "travel-map-settings.json";
    private const string LegacyFileName = "GPSSetting.xml";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _legacyPath;
    private readonly string _previousSettingsPath;
    private readonly TravelMapSettingsAtomicWriter _writer;
    private Dictionary<string, JsonElement>? _extensionData;

    public TravelMapSettingsStore(string directory, string? legacyPath = null)
        : this(directory, legacyPath, AtomicFile.ReplaceAsync)
    {
    }

    internal TravelMapSettingsStore(
        string directory,
        string? legacyPath,
        TravelMapSettingsAtomicWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);
        SettingsPath = Path.Combine(fullDirectory, SettingsFileName);
        _previousSettingsPath = Path.Combine(fullDirectory, PreviousSettingsFileName);
        _legacyPath = legacyPath is null
            ? Path.Combine(fullDirectory, LegacyFileName)
            : Path.GetFullPath(legacyPath);
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public string SettingsPath { get; }

    public bool IsReadOnly { get; private set; }

    internal void EnterReadOnlyMode() => IsReadOnly = true;

    public async Task<TravelMapSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadWithOutcomeAsync(cancellationToken).ConfigureAwait(false)).Settings;

    public async Task<TravelMapSettingsLoadResult> LoadWithOutcomeAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IsReadOnly = false;
            _extensionData = null;
            if (File.Exists(SettingsPath))
            {
                return await LoadExistingAsync(
                    SettingsPath,
                    cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(_previousSettingsPath))
            {
                var migrated = await LoadExistingAsync(
                    _previousSettingsPath,
                    cancellationToken).ConfigureAwait(false);
                return migrated.Outcome is TravelMapSettingsLoadOutcome.UnsupportedFutureSchemaReadOnly
                    or TravelMapSettingsLoadOutcome.CorruptIsolated
                        ? migrated
                        : migrated with { Outcome = TravelMapSettingsLoadOutcome.MigratedPreviousPath };
            }

            var settings = new TravelMapSettings();
            TryMigrateLegacyFlags(settings);
            settings.Normalize();
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            return new TravelMapSettingsLoadResult(
                settings,
                TravelMapSettingsLoadOutcome.Created,
                IsReadOnly: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        TravelMapSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsReadOnly)
            {
                await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TravelMapSettingsLoadResult> LoadExistingAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The settings document must be an object.");
            }

            var hasSchema = parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement);
            var schema = SchemaKind.Invalid;
            if (hasSchema)
            {
                schema = ClassifySchema(schemaElement);
                if (schema == SchemaKind.Future)
                {
                    IsReadOnly = true;
                    return new TravelMapSettingsLoadResult(
                        new TravelMapSettings(),
                        TravelMapSettingsLoadOutcome.UnsupportedFutureSchemaReadOnly,
                        IsReadOnly: true);
                }

                if (schema == SchemaKind.Invalid)
                {
                    throw new InvalidDataException("The settings schema version is invalid.");
                }
            }

            var document = JsonSerializer.Deserialize<SettingsDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The settings document is empty.");
            var settings = document.ToSettings();
            if (schema == SchemaKind.Previous && settings.MiniMapSize == 384)
            {
                settings.MiniMapSize = 192;
            }

            settings.Normalize();
            _extensionData = document.ExtensionData?
                .Where(pair => !string.Equals(pair.Key, "schemaVersion", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            return new TravelMapSettingsLoadResult(
                settings,
                schema switch
                {
                    SchemaKind.Previous => TravelMapSettingsLoadOutcome.MigratedPreviousSchema,
                    SchemaKind.Current => TravelMapSettingsLoadOutcome.Loaded,
                    _ => TravelMapSettingsLoadOutcome.MigratedUnversioned,
                },
                IsReadOnly: false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            IsolateCorruptSettings(sourcePath);
            var defaults = new TravelMapSettings();
            defaults.Normalize();
            await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
            return new TravelMapSettingsLoadResult(
                defaults,
                TravelMapSettingsLoadOutcome.CorruptIsolated,
                IsReadOnly: false);
        }
    }

    private static SchemaKind ClassifySchema(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            return SchemaKind.Invalid;
        }

        return ClassifySchemaNumber(element.GetRawText().AsSpan());
    }

    private static SchemaKind ClassifySchemaNumber(ReadOnlySpan<char> raw)
    {
        if (raw.IsEmpty || raw[0] == '-')
        {
            return SchemaKind.Invalid;
        }

        var index = 0;
        if (!IsAsciiDigit(raw[index]))
        {
            return SchemaKind.Invalid;
        }

        var integerStart = index;
        if (raw[index] == '0' && index + 1 < raw.Length && IsAsciiDigit(raw[index + 1]))
        {
            return SchemaKind.Invalid;
        }

        while (index < raw.Length && IsAsciiDigit(raw[index]))
        {
            index++;
        }

        var integerDigits = index - integerStart;
        if (index < raw.Length && raw[index] == '.')
        {
            index++;
            var fractionStart = index;
            while (index < raw.Length && IsAsciiDigit(raw[index]))
            {
                index++;
            }

            if (index == fractionStart)
            {
                return SchemaKind.Invalid;
            }
        }

        var significandEnd = index;
        if (!TryReadExponent(raw, ref index, out var exponent) || index != raw.Length)
        {
            return SchemaKind.Invalid;
        }

        var firstNonZero = -1;
        long leadingZeroDigits = 0;
        for (var digitIndex = integerStart; digitIndex < significandEnd; digitIndex++)
        {
            var digit = raw[digitIndex];
            if (digit == '.')
            {
                continue;
            }

            if (digit == '0')
            {
                leadingZeroDigits++;
                continue;
            }

            firstNonZero = digitIndex;
            break;
        }

        if (firstNonZero < 0)
        {
            return SchemaKind.Invalid;
        }

        var decimalPositionWithoutExponent = (long)integerDigits - leadingZeroDigits;
        var exponentForSingleIntegerDigit = 1L - decimalPositionWithoutExponent;
        if (exponent > exponentForSingleIntegerDigit)
        {
            return SchemaKind.Future;
        }

        if (exponent < exponentForSingleIntegerDigit)
        {
            return SchemaKind.Invalid;
        }

        var hasNonZeroTail = false;
        for (var digitIndex = firstNonZero + 1; digitIndex < significandEnd; digitIndex++)
        {
            if (raw[digitIndex] is not ('.' or '0'))
            {
                hasNonZeroTail = true;
                break;
            }
        }

        return raw[firstNonZero] switch
        {
            '1' when !hasNonZeroTail => SchemaKind.Previous,
            '2' when !hasNonZeroTail => SchemaKind.Current,
            >= '2' and <= '9' => SchemaKind.Future,
            _ => SchemaKind.Invalid,
        };
    }

    private static bool TryReadExponent(
        ReadOnlySpan<char> raw,
        ref int index,
        out long exponent)
    {
        exponent = 0;
        if (index == raw.Length)
        {
            return true;
        }

        if (raw[index] is not ('e' or 'E'))
        {
            return false;
        }

        index++;
        var isNegative = false;
        if (index < raw.Length && raw[index] is '+' or '-')
        {
            isNegative = raw[index] == '-';
            index++;
        }

        var exponentStart = index;
        long magnitude = 0;
        while (index < raw.Length && IsAsciiDigit(raw[index]))
        {
            var digit = raw[index] - '0';
            magnitude = magnitude <= (long.MaxValue - digit) / 10
                ? (magnitude * 10) + digit
                : long.MaxValue;
            index++;
        }

        if (index == exponentStart)
        {
            return false;
        }

        exponent = isNegative ? -magnitude : magnitude;
        return true;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private Task SaveCoreAsync(TravelMapSettings settings, CancellationToken cancellationToken)
    {
        var document = SettingsDocument.FromSettings(settings, _extensionData);
        return _writer(
            SettingsPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, document, SerializerOptions, token),
            cancellationToken);
    }

    private void TryMigrateLegacyFlags(TravelMapSettings settings)
    {
        if (!File.Exists(_legacyPath))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(_legacyPath);
            if (TryReadLegacyJson(text, out var displayMap, out var acceptInvitations)
                || TryReadLegacyXml(text, out displayMap, out acceptInvitations))
            {
                if (displayMap.HasValue)
                {
                    settings.IsMiniMapVisible = displayMap.Value;
                }

                if (acceptInvitations.HasValue)
                {
                    settings.AcceptTeleportInvitations = acceptInvitations.Value;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryReadLegacyJson(
        string text,
        out bool? displayMap,
        out bool? acceptInvitations)
    {
        displayMap = null;
        acceptInvitations = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            displayMap = ReadBoolean(document.RootElement, "isDisplayMap");
            acceptInvitations = ReadBoolean(document.RootElement, "isAllowTelePortRequest");
            return displayMap.HasValue || acceptInvitations.HasValue;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadLegacyXml(
        string text,
        out bool? displayMap,
        out bool? acceptInvitations)
    {
        displayMap = null;
        acceptInvitations = null;
        try
        {
            var root = XDocument.Parse(text).Root;
            if (root is null)
            {
                return false;
            }

            displayMap = ReadBoolean(root, "isDisplayMap");
            acceptInvitations = ReadBoolean(root, "isAllowTelePortRequest");
            return displayMap.HasValue || acceptInvitations.HasValue;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool? ReadBoolean(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool? ReadBoolean(XElement root, string name)
    {
        var value = root.Attribute(name)?.Value
            ?? root.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        return bool.TryParse(value, out var result) ? result : null;
    }

    private static void IsolateCorruptSettings(string path)
    {
        var corruptPath = path + ".corrupt";
        for (var suffix = 1; File.Exists(corruptPath) || Directory.Exists(corruptPath); suffix++)
        {
            corruptPath = path + $".corrupt.{suffix}";
        }

        File.Move(path, corruptPath);
    }

    private enum SchemaKind
    {
        Invalid,
        Previous,
        Current,
        Future,
    }

    private sealed class SettingsDocument
    {
        [JsonPropertyName("schemaVersion")]
        public JsonElement SchemaVersion { get; set; } =
            JsonSerializer.SerializeToElement(CurrentSchemaVersion);

        public bool IsMiniMapVisible { get; set; } = true;

        public bool ShowCoordinates { get; set; } = true;

        public bool UseDayNightTint { get; set; } = true;

        public bool AcceptTeleportInvitations { get; set; } = true;

        public int MiniMapSize { get; set; } = 160;

        public float MiniMapBlocksPerPixel { get; set; } = 1f;

        public float LargeMapBlocksPerPixel { get; set; } = 2f;

        public string LargeMapHotkey { get; set; } = "M";

        public float NightMinimumBrightness { get; set; } = 0.4f;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        public TravelMapSettings ToSettings() => new()
        {
            IsMiniMapVisible = IsMiniMapVisible,
            ShowCoordinates = ShowCoordinates,
            UseDayNightTint = UseDayNightTint,
            AcceptTeleportInvitations = AcceptTeleportInvitations,
            MiniMapSize = MiniMapSize,
            MiniMapBlocksPerPixel = MiniMapBlocksPerPixel,
            LargeMapBlocksPerPixel = LargeMapBlocksPerPixel,
            LargeMapHotkey = LargeMapHotkey,
            NightMinimumBrightness = NightMinimumBrightness,
        };

        public static SettingsDocument FromSettings(
            TravelMapSettings settings,
            Dictionary<string, JsonElement>? extensionData) => new()
        {
            SchemaVersion = JsonSerializer.SerializeToElement(CurrentSchemaVersion),
            IsMiniMapVisible = settings.IsMiniMapVisible,
            ShowCoordinates = settings.ShowCoordinates,
            UseDayNightTint = settings.UseDayNightTint,
            AcceptTeleportInvitations = settings.AcceptTeleportInvitations,
            MiniMapSize = settings.MiniMapSize,
            MiniMapBlocksPerPixel = settings.MiniMapBlocksPerPixel,
            LargeMapBlocksPerPixel = settings.LargeMapBlocksPerPixel,
            LargeMapHotkey = settings.LargeMapHotkey,
            NightMinimumBrightness = settings.NightMinimumBrightness,
            ExtensionData = extensionData,
        };
    }
}
