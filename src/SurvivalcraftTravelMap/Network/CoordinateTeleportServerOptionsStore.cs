using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;

namespace SurvivalcraftTravelMap.Network;

public enum CoordinateTeleportServerOptionsLoadOutcome
{
    Loaded,
    Created,
    CorruptIsolated,
    UnsupportedFutureSchemaReadOnly,
}

public sealed record CoordinateTeleportServerOptionsLoadResult(
    CoordinateTeleportServerOptions Options,
    CoordinateTeleportServerOptionsLoadOutcome Outcome);

public sealed class CoordinateTeleportFutureSchemaWarningGate
{
    private int _notified;

    public void NotifyIfNeeded(
        CoordinateTeleportServerOptionsLoadResult result,
        Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(notify);
        if (result.Outcome == CoordinateTeleportServerOptionsLoadOutcome.UnsupportedFutureSchemaReadOnly
            && Interlocked.Exchange(ref _notified, 1) == 0)
        {
            notify("A future server-settings schema was loaded read-only; safe defaults are active.");
        }
    }
}

public sealed class CoordinateTeleportServerOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _path;
    private bool _futureSchemaReadOnly;

    public CoordinateTeleportServerOptionsStore(string path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Server settings path is required.", nameof(path))
            : Path.GetFullPath(path);
    }

    public CoordinateTeleportServerOptions Load()
    {
        return LoadWithOutcome().Options;
    }

    public CoordinateTeleportServerOptionsLoadResult LoadWithOutcome()
    {
        if (!File.Exists(_path))
        {
            _futureSchemaReadOnly = false;
            var defaults = new CoordinateTeleportServerOptions();
            Save(defaults);
            return new CoordinateTeleportServerOptionsLoadResult(
                defaults,
                CoordinateTeleportServerOptionsLoadOutcome.Created);
        }

        try
        {
            var json = File.ReadAllText(_path);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidDataException("Server settings schema version is missing or invalid.");
            }

            if (!schemaElement.TryGetInt32(out var schemaVersion))
            {
                var rawSchema = schemaElement.GetRawText();
                if ((BigInteger.TryParse(
                        rawSchema,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var largeInteger)
                        && largeInteger > BigInteger.One)
                    || (double.TryParse(
                        rawSchema,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var largeNumber)
                        && largeNumber > 1d))
                {
                    return FutureSchemaResult();
                }

                throw new InvalidDataException("Server settings schema version is invalid.");
            }

            if (schemaVersion > 1)
            {
                return FutureSchemaResult();
            }

            if (schemaVersion != 1)
            {
                throw new InvalidDataException("Server settings schema version is missing or invalid.");
            }

            var document = JsonSerializer.Deserialize<ServerOptionsDocument>(
                json,
                JsonOptions) ?? throw new InvalidDataException("Server settings document is empty.");

            _futureSchemaReadOnly = false;
            return new CoordinateTeleportServerOptionsLoadResult(
                new CoordinateTeleportServerOptions
                {
                SurfaceTeleportEnabled = document.SurfaceTeleportEnabled ?? true,
                WaypointTeleportEnabled = document.WaypointTeleportEnabled ?? true,
                },
                CoordinateTeleportServerOptionsLoadOutcome.Loaded);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _futureSchemaReadOnly = false;
            IsolateCorruptFile();
            var defaults = new CoordinateTeleportServerOptions();
            Save(defaults);
            return new CoordinateTeleportServerOptionsLoadResult(
                defaults,
                CoordinateTeleportServerOptionsLoadOutcome.CorruptIsolated);
        }
    }

    private CoordinateTeleportServerOptionsLoadResult FutureSchemaResult()
    {
        _futureSchemaReadOnly = true;
        return new CoordinateTeleportServerOptionsLoadResult(
            new CoordinateTeleportServerOptions(),
            CoordinateTeleportServerOptionsLoadOutcome.UnsupportedFutureSchemaReadOnly);
    }

    public void Save(CoordinateTeleportServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_futureSchemaReadOnly)
        {
            throw new InvalidOperationException(
                "A future server-settings schema is loaded read-only and cannot be overwritten.");
        }

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Server settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(
            new ServerOptionsDocument
            {
                SchemaVersion = 1,
                SurfaceTeleportEnabled = options.SurfaceTeleportEnabled,
                WaypointTeleportEnabled = options.WaypointTeleportEnabled,
            },
            JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private void IsolateCorruptFile()
    {
        var corruptPath = _path + ".corrupt";
        File.Move(_path, corruptPath, overwrite: true);
    }

    private sealed class ServerOptionsDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; set; }

        [JsonPropertyName("surfaceTeleportEnabled")]
        public bool? SurfaceTeleportEnabled { get; set; }

        [JsonPropertyName("waypointTeleportEnabled")]
        public bool? WaypointTeleportEnabled { get; set; }
    }
}
