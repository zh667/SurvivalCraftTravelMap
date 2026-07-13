using System.Text.Json;
using System.Xml.Linq;
using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Settings;

public sealed class TravelMapSettingsStore
{
    private const string SettingsFileName = "travel-map-settings.json";
    private const string LegacyFileName = "GPSSetting.xml";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _legacyPath;

    public TravelMapSettingsStore(string directory, string? legacyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);
        SettingsPath = Path.Combine(fullDirectory, SettingsFileName);
        _legacyPath = legacyPath is null
            ? Path.Combine(fullDirectory, LegacyFileName)
            : Path.GetFullPath(legacyPath);
    }

    public string SettingsPath { get; }

    public async Task<TravelMapSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TravelMapSettings settings;
            if (File.Exists(SettingsPath))
            {
                try
                {
                    await using var stream = new FileStream(
                        SettingsPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        4096,
                        FileOptions.Asynchronous);
                    settings = await JsonSerializer.DeserializeAsync<TravelMapSettings>(
                            stream,
                            SerializerOptions,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new JsonException("The settings document is null.");
                }
                catch (JsonException)
                {
                    IsolateCorruptSettings();
                    settings = new TravelMapSettings();
                }
            }
            else
            {
                settings = new TravelMapSettings();
                TryMigrateLegacyFlags(settings);
            }

            settings.Normalize();
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            return settings;
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
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task SaveCoreAsync(TravelMapSettings settings, CancellationToken cancellationToken) =>
        AtomicFile.ReplaceAsync(
            SettingsPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, token),
            cancellationToken);

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

    private void IsolateCorruptSettings()
    {
        var corruptPath = SettingsPath + ".corrupt";
        for (var suffix = 1; File.Exists(corruptPath) || Directory.Exists(corruptPath); suffix++)
        {
            corruptPath = SettingsPath + $".corrupt.{suffix}";
        }

        File.Move(SettingsPath, corruptPath);
    }
}
