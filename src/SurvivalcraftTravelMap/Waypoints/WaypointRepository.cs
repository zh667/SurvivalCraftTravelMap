using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SurvivalcraftTravelMap.Persistence;

namespace SurvivalcraftTravelMap.Waypoints;

public sealed class WaypointRepository
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "waypoints.json";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock;
    private List<Waypoint> _waypoints = [];
    private bool _isReadOnly;
    private string _readOnlyError = string.Empty;

    public WaypointRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.GetFullPath(Path.Combine(directory, FileName));
        _fileLock = FileLocks.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public bool IsReadOnly
    {
        get
        {
            lock (_sync)
            {
                return _isReadOnly;
            }
        }
    }

    public string ReadOnlyError
    {
        get
        {
            lock (_sync)
            {
                return _readOnlyError;
            }
        }
    }

    public Waypoint Add(string name, Vector3 position)
    {
        lock (_sync)
        {
            EnsureWritable();
            var waypoint = new Waypoint(
                Guid.NewGuid(),
                NormalizeName(name),
                ValidatePosition(position),
                DateTimeOffset.UtcNow);
            _waypoints.Add(waypoint);
            return waypoint;
        }
    }

    public bool Rename(Guid id, string name)
    {
        lock (_sync)
        {
            EnsureWritable();
            var normalizedName = NormalizeName(name);
            var index = _waypoints.FindIndex(waypoint => waypoint.Id == id);
            if (index < 0)
            {
                return false;
            }

            _waypoints[index] = _waypoints[index] with { Name = normalizedName };
            return true;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_sync)
        {
            EnsureWritable();
            var index = _waypoints.FindIndex(waypoint => waypoint.Id == id);
            if (index < 0)
            {
                return false;
            }

            _waypoints.RemoveAt(index);
            return true;
        }
    }

    public IReadOnlyList<Waypoint> GetAll()
    {
        lock (_sync)
        {
            return new ReadOnlyCollection<Waypoint>(_waypoints.ToArray());
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                ReplaceWritableState([]);
                return;
            }

            var json = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                ResetAfterCorruption(cancellationToken);
                return;
            }

            using (document)
            {
                JsonElement schemaElement;
                try
                {
                    schemaElement = document.RootElement.GetProperty("schemaVersion");
                }
                catch (InvalidOperationException)
                {
                    ResetAfterCorruption(cancellationToken);
                    return;
                }
                catch (KeyNotFoundException)
                {
                    ResetAfterCorruption(cancellationToken);
                    return;
                }

                if (schemaElement.ValueKind != JsonValueKind.Number)
                {
                    ResetAfterCorruption(cancellationToken);
                    return;
                }

                if (!schemaElement.TryGetInt32(out var schemaVersion))
                {
                    EnterReadOnly(schemaElement.GetRawText());
                    return;
                }

                if (schemaVersion != CurrentSchemaVersion)
                {
                    EnterReadOnly(schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }

            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedRepository>(json, SerializerOptions)
                    ?? throw new JsonException("The waypoint repository is empty.");
                var loaded = ValidateAndConvert(
                    persisted.Waypoints
                    ?? throw new JsonException("The waypoint list cannot be null."));
                ReplaceWritableState(loaded);
            }
            catch (JsonException)
            {
                ResetAfterCorruption(cancellationToken);
            }
            catch (InvalidDataException)
            {
                ResetAfterCorruption(cancellationToken);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        PersistedRepository persisted;
        lock (_sync)
        {
            EnsureWritable();
            persisted = new PersistedRepository
            {
                SchemaVersion = CurrentSchemaVersion,
                Waypoints = _waypoints.Select(PersistedWaypoint.FromWaypoint).ToArray(),
            };
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.ReplaceAsync(
                _filePath,
                (stream, token) => JsonSerializer.SerializeAsync(
                    stream,
                    persisted,
                    SerializerOptions,
                    token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Waypoint names cannot be blank.", nameof(name));
        }

        return normalized;
    }

    private static Vector3 ValidatePosition(Vector3 position)
    {
        if (!float.IsFinite(position.X)
            || !float.IsFinite(position.Y)
            || !float.IsFinite(position.Z))
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "Waypoint coordinates must be finite.");
        }

        return position;
    }

    private static List<Waypoint> ValidateAndConvert(PersistedWaypoint[] persistedWaypoints)
    {
        var waypoints = new List<Waypoint>(persistedWaypoints.Length);
        var ids = new HashSet<Guid>();
        foreach (var persisted in persistedWaypoints)
        {
            if (persisted.Id == Guid.Empty || !ids.Add(persisted.Id))
            {
                throw new InvalidDataException("Waypoint IDs must be non-empty and unique.");
            }

            string name;
            Vector3 position;
            try
            {
                name = NormalizeName(persisted.Name);
                position = ValidatePosition(new Vector3(persisted.X, persisted.Y, persisted.Z));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The waypoint repository contains invalid data.", exception);
            }

            if (persisted.CreatedAt == default)
            {
                throw new InvalidDataException("Waypoint creation times must be present.");
            }

            waypoints.Add(new Waypoint(
                persisted.Id,
                name,
                position,
                persisted.CreatedAt.ToUniversalTime()));
        }

        return waypoints;
    }

    private void EnsureWritable()
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException(_readOnlyError);
        }
    }

    private void ResetAfterCorruption(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsolateCorruptFile();
        ReplaceWritableState([]);
    }

    private void ReplaceWritableState(List<Waypoint> waypoints)
    {
        lock (_sync)
        {
            _waypoints = waypoints;
            _isReadOnly = false;
            _readOnlyError = string.Empty;
        }
    }

    private void EnterReadOnly(string schemaVersion)
    {
        lock (_sync)
        {
            _waypoints = [];
            _isReadOnly = true;
            _readOnlyError = $"Unsupported waypoint schema version {schemaVersion}.";
        }
    }

    private void IsolateCorruptFile()
    {
        var corruptPath = _filePath + ".corrupt";
        for (var suffix = 1; File.Exists(corruptPath) || Directory.Exists(corruptPath); suffix++)
        {
            corruptPath = _filePath + $".corrupt.{suffix}";
        }

        File.Move(_filePath, corruptPath);
    }

    private sealed class PersistedRepository
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("waypoints")]
        public PersistedWaypoint[]? Waypoints { get; init; } = [];
    }

    private sealed class PersistedWaypoint
    {
        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("x")]
        public float X { get; init; }

        [JsonPropertyName("y")]
        public float Y { get; init; }

        [JsonPropertyName("z")]
        public float Z { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; init; }

        public static PersistedWaypoint FromWaypoint(Waypoint waypoint) => new()
        {
            Id = waypoint.Id,
            Name = waypoint.Name,
            X = waypoint.Position.X,
            Y = waypoint.Position.Y,
            Z = waypoint.Position.Z,
            CreatedAt = waypoint.CreatedAt,
        };
    }
}
