using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SurvivalcraftTravelMap.Map;

namespace SurvivalcraftTravelMap.Persistence;

public static class TileCodec
{
    private const byte LegacyPaletteVersion = 1;
    private const byte PaletteOnlyVersion = 2;
    private const byte CurrentVersion = 3;
    private const int MagicLength = 4;
    private const int HeaderLength = MagicLength + 1 + sizeof(int) + sizeof(int);
    private const int ChecksumLength = 32;
    private const int PaletteOnlyPayloadLength =
        HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount + ChecksumLength;
    private const int CurrentPayloadLength = PaletteOnlyPayloadLength + MapTile.HeightShadeByteCount;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCTM");

    public static void Write(Stream destination, MapTile tile)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tile);

        var payload = new byte[CurrentPayloadLength];
        Magic.CopyTo(payload, 0);
        payload[MagicLength] = CurrentVersion;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(MagicLength + 1), tile.TileX);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(MagicLength + 1 + sizeof(int)), tile.TileZ);
        tile.CopyExploredTo(payload.AsSpan(HeaderLength, MapTile.ExploredByteCount));
        tile.CopyColorsTo(payload.AsSpan(HeaderLength + MapTile.ExploredByteCount, MapTile.ColorByteCount));
        tile.CopyHeightShadesTo(payload.AsSpan(
            HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount,
            MapTile.HeightShadeByteCount));

        var checksumOffset = CurrentPayloadLength - ChecksumLength;
        SHA256.HashData(payload.AsSpan(MagicLength, checksumOffset - MagicLength), payload.AsSpan(checksumOffset));

        using var deflate = new DeflateStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(payload);
    }

    public static MapTile Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var payload = new byte[CurrentPayloadLength];
        var payloadLength = 0;
        try
        {
            using var deflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
            while (payloadLength < payload.Length)
            {
                var count = deflate.Read(payload.AsSpan(payloadLength));
                if (count == 0)
                {
                    break;
                }

                payloadLength += count;
            }

            if (deflate.ReadByte() != -1)
            {
                throw new InvalidDataException("Tile payload has trailing uncompressed data.");
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Tile payload has an invalid length.", exception);
        }

        if (payloadLength < MagicLength + 1
            || !payload.AsSpan(0, MagicLength).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Tile magic is invalid.");
        }

        var version = payload[MagicLength];
        if (version is not LegacyPaletteVersion and not PaletteOnlyVersion and not CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported tile version {version}.");
        }

        var expectedPayloadLength = version == CurrentVersion
            ? CurrentPayloadLength
            : PaletteOnlyPayloadLength;
        if (payloadLength != expectedPayloadLength)
        {
            throw new InvalidDataException("Tile payload has an invalid length.");
        }

        var checksumOffset = expectedPayloadLength - ChecksumLength;
        Span<byte> expectedChecksum = stackalloc byte[ChecksumLength];
        SHA256.HashData(payload.AsSpan(MagicLength, checksumOffset - MagicLength), expectedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(
                expectedChecksum,
                payload.AsSpan(checksumOffset, ChecksumLength)))
        {
            throw new InvalidDataException("Tile checksum is invalid.");
        }

        var tileX = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(MagicLength + 1));
        var tileZ = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(MagicLength + 1 + sizeof(int)));
        var explored = payload.AsSpan(HeaderLength, MapTile.ExploredByteCount).ToArray();
        var colors = payload.AsSpan(HeaderLength + MapTile.ExploredByteCount, MapTile.ColorByteCount).ToArray();
        if (version == LegacyPaletteVersion)
        {
            LegacyTerrainPaletteMigration.UpgradeExploredColors(explored, colors);
        }

        var heightShades = version == CurrentVersion
            ? payload.AsSpan(
                HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount,
                MapTile.HeightShadeByteCount).ToArray()
            : new byte[MapTile.HeightShadeByteCount];

        return new MapTile(tileX, tileZ, explored, colors, heightShades);
    }
}
