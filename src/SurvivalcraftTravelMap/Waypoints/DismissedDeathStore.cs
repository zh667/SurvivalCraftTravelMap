using System.Text.Json;
using System.Text.Json.Serialization;
using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Waypoints;

/// <summary>
/// Per-player, per-world persistence for the set of death records the player chose to dismiss.
/// The native game stores its death history in its own save and offers no delete API, so the mod
/// records each dismissed death's <see cref="DeathMarkerIdentity"/> in a sibling
/// <c>death-state.json</c> next to <c>waypoints.json</c> and simply stops drawing those deaths.
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
    private readonly HashSet<DeathMarkerIdentity> _dismissed = new();

    public DismissedDeathStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.GetFullPath(Path.Combine(directory, FileName));
    }

    /// <summary>A snapshot of every death identity the player has dismissed.</summary>
    public IReadOnlySet<DeathMarkerIdentity> Dismissed
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<DeathMarkerIdentity>(_dismissed);
            }
        }
    }

    public bool Contains(DeathMarkerIdentity identity)
    {
        lock (_sync)
        {
            return _dismissed.Contains(identity);
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            lock (_sync)
            {
                _dismissed.Clear();
            }

            return;
        }

        var loaded = new HashSet<DeathMarkerIdentity>();
        try
        {
            var json = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<PersistedDeathState>(json);
            if (document?.DismissedDeaths is { } entries)
            {
                foreach (var entry in entries)
                {
                    if (DeathMarkerIdentity.TryParse(entry, out var identity))
                    {
                        loaded.Add(identity);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt death-state file simply means nothing is dismissed; keep drawing markers.
            loaded.Clear();
        }

        lock (_sync)
        {
            _dismissed.Clear();
            _dismissed.UnionWith(loaded);
        }
    }

    /// <summary>Adds a death identity to the dismissed set and persists the whole set.</summary>
    public async Task AddAsync(DeathMarkerIdentity identity, CancellationToken cancellationToken = default)
    {
        string[] snapshot;
        lock (_sync)
        {
            _dismissed.Add(identity);
            snapshot = _dismissed.Select(entry => entry.Serialize()).ToArray();
        }

        var document = new PersistedDeathState
        {
            SchemaVersion = CurrentSchemaVersion,
            DismissedDeaths = snapshot,
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

        [JsonPropertyName("dismissedDeaths")]
        public IReadOnlyList<string>? DismissedDeaths { get; init; }
    }
}
