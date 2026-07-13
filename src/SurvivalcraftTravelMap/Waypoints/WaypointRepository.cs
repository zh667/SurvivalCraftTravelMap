using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SurvivalcraftTravelMap.Persistence;

[assembly: InternalsVisibleTo("SurvivalcraftTravelMap.Tests")]

namespace SurvivalcraftTravelMap.Waypoints;

internal interface IWaypointRepositoryFileAccess
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    Task ReplaceAsync(
        string path,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken);

    void Move(string sourcePath, string destinationPath);
}

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
    private readonly IWaypointRepositoryFileAccess _fileAccess;
    private List<Waypoint> _waypoints = [];
    private bool _isReadOnly;
    private int _activeLoadCount;
    private string _readOnlyError = string.Empty;

    public WaypointRepository(string directory)
        : this(directory, PhysicalWaypointRepositoryFileAccess.Instance)
    {
    }

    internal WaypointRepository(string directory, IWaypointRepositoryFileAccess fileAccess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fileAccess);
        _filePath = Path.GetFullPath(Path.Combine(directory, FileName));
        _fileLock = FileLocks.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
        _fileAccess = fileAccess;
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
        BeginLoad();
        var fileLockAcquired = false;
        try
        {
            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            fileLockAcquired = true;

            if (!_fileAccess.FileExists(_filePath))
            {
                ReplaceWritableState([]);
                return;
            }

            var json = await _fileAccess.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
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

                try
                {
                    ReplaceWritableState(ParseWaypoints(document.RootElement));
                }
                catch (InvalidDataException)
                {
                    ResetAfterCorruption(cancellationToken);
                }
            }
        }
        finally
        {
            if (fileLockAcquired)
            {
                _fileLock.Release();
            }

            EndLoad();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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

            await _fileAccess.ReplaceAsync(
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

    private static List<Waypoint> ParseWaypoints(JsonElement root)
    {
        var persistedWaypoints = GetRequiredProperty(root, "waypoints", JsonValueKind.Array);
        var waypoints = new List<Waypoint>(persistedWaypoints.GetArrayLength());
        var ids = new HashSet<Guid>();
        foreach (var persisted in persistedWaypoints.EnumerateArray())
        {
            var idElement = GetRequiredProperty(persisted, "id", JsonValueKind.String);
            if (!idElement.TryGetGuid(out var id) || id == Guid.Empty || !ids.Add(id))
            {
                throw new InvalidDataException("Waypoint IDs must be non-empty and unique.");
            }

            string name;
            try
            {
                name = NormalizeName(
                    GetRequiredProperty(persisted, "name", JsonValueKind.String).GetString()
                    ?? throw new InvalidDataException("Waypoint names cannot be null."));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The waypoint repository contains an invalid name.", exception);
            }

            var position = new Vector3(
                GetRequiredFiniteSingle(persisted, "x"),
                GetRequiredFiniteSingle(persisted, "y"),
                GetRequiredFiniteSingle(persisted, "z"));
            var createdAtElement = GetRequiredProperty(
                persisted,
                "createdAt",
                JsonValueKind.String);
            if (!createdAtElement.TryGetDateTimeOffset(out var createdAt) || createdAt == default)
            {
                throw new InvalidDataException("Waypoint creation times must be valid.");
            }

            waypoints.Add(new Waypoint(
                id,
                name,
                position,
                createdAt.ToUniversalTime()));
        }

        return waypoints;
    }

    private static JsonElement GetRequiredProperty(
        JsonElement parent,
        string propertyName,
        JsonValueKind expectedKind)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != expectedKind)
        {
            throw new InvalidDataException(
                $"Required waypoint property '{propertyName}' must be {expectedKind}.");
        }

        return property;
    }

    private static float GetRequiredFiniteSingle(JsonElement parent, string propertyName)
    {
        var property = GetRequiredProperty(parent, propertyName, JsonValueKind.Number);
        if (!property.TryGetSingle(out var value) || !float.IsFinite(value))
        {
            throw new InvalidDataException(
                $"Waypoint coordinate '{propertyName}' must be a finite single-precision number.");
        }

        return value;
    }

    private void EnsureWritable()
    {
        if (_activeLoadCount > 0)
        {
            throw new InvalidOperationException("The waypoint repository is loading.");
        }

        if (_isReadOnly)
        {
            throw new InvalidOperationException(_readOnlyError);
        }
    }

    private void BeginLoad()
    {
        lock (_sync)
        {
            _activeLoadCount++;
        }
    }

    private void EndLoad()
    {
        lock (_sync)
        {
            _activeLoadCount--;
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
        for (var suffix = 1;
             _fileAccess.FileExists(corruptPath) || _fileAccess.DirectoryExists(corruptPath);
             suffix++)
        {
            corruptPath = _filePath + $".corrupt.{suffix}";
        }

        _fileAccess.Move(_filePath, corruptPath);
    }

    private sealed class PersistedRepository
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("waypoints")]
        public PersistedWaypoint[] Waypoints { get; init; } = [];
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

internal sealed class PhysicalWaypointRepositoryFileAccess : IWaypointRepositoryFileAccess
{
    public static PhysicalWaypointRepositoryFileAccess Instance { get; } = new();

    private PhysicalWaypointRepositoryFileAccess()
    {
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    public Task ReplaceAsync(
        string path,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken) =>
        AtomicFile.ReplaceAsync(path, writeAsync, cancellationToken);

    public void Move(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath);
}
