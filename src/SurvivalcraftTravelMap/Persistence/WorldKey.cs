using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SurvivalcraftTravelMap.Persistence;

public static class WorldKey
{
    public static string ForLocal(string worldDirectoryIdentifier) => Hash(Normalize(worldDirectoryIdentifier));

    /// <summary>
    /// Key of a local world by the full system path of its folder, so same-named worlds of two
    /// installs (or a test copy) no longer share a cache. Separators are unified, trailing ones
    /// dropped and case folded (Windows paths are case-insensitive).
    /// </summary>
    public static string ForLocalPath(string worldSystemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldSystemPath);
        var path = worldSystemPath.Trim();
        // Resolve relative parts on the platform that produced the path; elsewhere (tests) a
        // drive-letter path is already absolute and must not be prefixed with the working directory.
        var isDrivePath = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        if (!isDrivePath || OperatingSystem.IsWindows())
        {
            path = Path.GetFullPath(path);
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The path must contain a non-separator character.", nameof(worldSystemPath));
        }

        return Hash("path:" + normalized);
    }

    public static string ForServer(string host, int port, string serverWorldIdentifier)
    {
        if ((uint)port > ushort.MaxValue || port == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var normalized = string.Join(
            ':',
            Normalize(host),
            port.ToString(CultureInfo.InvariantCulture),
            Normalize(serverWorldIdentifier));
        return Hash(normalized);
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().TrimEnd('/', '\\').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The identifier must contain a non-separator character.", nameof(value));
        }

        return normalized;
    }

    private static string Hash(string normalized)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..24];
    }
}
