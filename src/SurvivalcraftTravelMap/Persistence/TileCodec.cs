using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SurvivalcraftTravelMap.Map;

namespace SurvivalcraftTravelMap.Persistence;

public static class TileCodec
{
    private const byte CurrentVersion = 1;
    private const int MagicLength = 4;
    private const int HeaderLength = MagicLength + 1 + sizeof(int) + sizeof(int);
    private const int ChecksumLength = 32;
    private const int PayloadLength = HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount + ChecksumLength;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCTM");

    public static void Write(Stream destination, MapTile tile)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tile);

        var payload = new byte[PayloadLength];
        Magic.CopyTo(payload, 0);
        payload[MagicLength] = CurrentVersion;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(MagicLength + 1), tile.TileX);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(MagicLength + 1 + sizeof(int)), tile.TileZ);
        tile.CopyExploredTo(payload.AsSpan(HeaderLength, MapTile.ExploredByteCount));
        tile.CopyColorsTo(payload.AsSpan(HeaderLength + MapTile.ExploredByteCount, MapTile.ColorByteCount));

        var checksumOffset = PayloadLength - ChecksumLength;
        SHA256.HashData(payload.AsSpan(MagicLength, checksumOffset - MagicLength), payload.AsSpan(checksumOffset));

        using var deflate = new DeflateStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(payload);
    }

    public static MapTile Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var payload = new byte[PayloadLength];
        try
        {
            using var deflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
            deflate.ReadExactly(payload);
            if (deflate.ReadByte() != -1)
            {
                throw new InvalidDataException("Tile payload has trailing uncompressed data.");
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Tile payload has an invalid length.", exception);
        }

        if (!payload.AsSpan(0, MagicLength).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Tile magic is invalid.");
        }

        if (payload[MagicLength] != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported tile version {payload[MagicLength]}.");
        }

        var checksumOffset = PayloadLength - ChecksumLength;
        Span<byte> expectedChecksum = stackalloc byte[ChecksumLength];
        SHA256.HashData(payload.AsSpan(MagicLength, checksumOffset - MagicLength), expectedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, payload.AsSpan(checksumOffset)))
        {
            throw new InvalidDataException("Tile checksum is invalid.");
        }

        var tileX = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(MagicLength + 1));
        var tileZ = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(MagicLength + 1 + sizeof(int)));
        var explored = payload.AsSpan(HeaderLength, MapTile.ExploredByteCount).ToArray();
        var colors = payload.AsSpan(HeaderLength + MapTile.ExploredByteCount, MapTile.ColorByteCount).ToArray();
        return new MapTile(tileX, tileZ, explored, colors);
    }
}
