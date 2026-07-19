using System.Text.Json;
using System.Text.Json.Serialization;
using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Waypoints;

/// <summary>
/// Per-player, per-world persistence for the single death record the player chose to dismiss.
/// The native game stores its death history in its own save and offers no delete API, so the mod
/// records the dismissed death's <see cref="DeathMarkerIdentity"/> in a sibling
/// <c>death-state.json</c> next to <c>waypoints.json</c> and simply stops drawing that death.
/// A newer death (a later record) is unaffected and re-shows a fresh tracked marker.
/// </summary>
public sealed class DismissedDeathStore
{
    private const string FileName = "death-state.json";
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private DeathMarkerIdentity? _dismissed;

    public DismissedDeathStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.GetFullPath(Path.Combine(directory, FileName));
    }

    public DeathMarkerIdentity? Dismissed
    {
        get
        {
            lock (_sync)
            {
                return _dismissed;
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            lock (_sync)
            {
                _dismissed = null;
            }

            return;
        }

        DeathMarkerIdentity? loaded = null;
        try
        {
            var json = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<PersistedDeathState>(json);
            if (document is not null
                && DeathMarkerIdentity.TryParse(document.DismissedDeath, out var identity))
            {
                loaded = identity;
            }
        }
        catch (JsonException)
        {
            // A corrupt death-state file simply means nothing is dismissed; keep drawing markers.
            loaded = null;
        }

        lock (_sync)
        {
            _dismissed = loaded;
        }
    }

    public async Task SetAsync(DeathMarkerIdentity? identity, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _dismissed = identity;
        }

        var document = new PersistedDeathState
        {
            SchemaVersion = CurrentSchemaVersion,
            DismissedDeath = identity?.Serialize(),
        };
        await AtomicFile.ReplaceAsync(
            _filePath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, document, SerializerOptions, token),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class PersistedDeathState
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("dismissedDeath")]
        public string? DismissedDeath { get; init; }
    }
}
