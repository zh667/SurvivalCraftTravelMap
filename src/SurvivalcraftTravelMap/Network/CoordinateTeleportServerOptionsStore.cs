using System.Text.Json;

namespace SurvivalcraftTravelMap.Network;

public sealed class CoordinateTeleportServerOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _path;

    public CoordinateTeleportServerOptionsStore(string path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Server settings path is required.", nameof(path))
            : Path.GetFullPath(path);
    }

    public CoordinateTeleportServerOptions Load()
    {
        if (!File.Exists(_path))
        {
            var defaults = new CoordinateTeleportServerOptions();
            Save(defaults);
            return defaults;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ServerOptionsDocument>(
                File.ReadAllText(_path),
                JsonOptions) ?? throw new InvalidDataException("Server settings document is empty.");
            return new CoordinateTeleportServerOptions
            {
                SurfaceTeleportEnabled = document.SurfaceTeleportEnabled ?? true,
                WaypointTeleportEnabled = document.WaypointTeleportEnabled ?? true,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            IsolateCorruptFile();
            var defaults = new CoordinateTeleportServerOptions();
            Save(defaults);
            return defaults;
        }
    }

    public void Save(CoordinateTeleportServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
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
        public int SchemaVersion { get; set; } = 1;

        public bool? SurfaceTeleportEnabled { get; set; }

        public bool? WaypointTeleportEnabled { get; set; }
    }
}
