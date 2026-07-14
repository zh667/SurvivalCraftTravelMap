using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SurvivalcraftTravelMap.Persistence;

public static class WorldKey
{
    public static string ForLocal(string worldDirectoryIdentifier) => Hash(Normalize(worldDirectoryIdentifier));

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
