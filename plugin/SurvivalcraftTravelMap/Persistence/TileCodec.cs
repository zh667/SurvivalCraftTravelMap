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
    private const byte HeightShadeVersion = 3;

    // Version 4 appends one MapSeasonCode per pixel after the height shades.
    private const byte SeasonVersion = 4;

    // Version 5 (2.9.0) appends a 2-byte little-endian mask of chunks whose height shades are
    // still the mirrored pre-2.9.0 values (see MapTile.IsChunkShadeCurrent). Its shades are lit
    // from world +X / +Z; versions 1-4 were lit from world -X / -Z.
    private const byte CurrentVersion = 5;
    private const int StaleShadeMaskLength = sizeof(ushort);
    private const int MagicLength = 4;
    private const int HeaderLength = MagicLength + 1 + sizeof(int) + sizeof(int);
    private const int ChecksumLength = 32;
    private const int PaletteOnlyPayloadLength =
        HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount + ChecksumLength;
    private const int HeightShadePayloadLength = PaletteOnlyPayloadLength + MapTile.HeightShadeByteCount;
    private const int SeasonPayloadLength = HeightShadePayloadLength + MapTile.SeasonByteCount;
    private const int CurrentPayloadLength = SeasonPayloadLength + StaleShadeMaskLength;
    private const int SeasonOffset =
        HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount + MapTile.HeightShadeByteCount;
    private const int StaleShadeMaskOffset = SeasonOffset + MapTile.SeasonByteCount;
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
        tile.CopySeasonsTo(payload.AsSpan(SeasonOffset, MapTile.SeasonByteCount));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(StaleShadeMaskOffset, StaleShadeMaskLength),
            tile.StaleShadeChunks);

        var checksumOffset = CurrentPayloadLength - ChecksumLength;
        SHA256.HashData(payload.AsSpan(MagicLength, checksumOffset - MagicLength), payload.AsSpan(checksumOffset));

        using var deflate = new DeflateStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(payload);
    }

    /// <param name="mirrorLegacyHeightShades">
    /// True for surface tiles: shades written before 2.9.0 are mirrored around neutral (their
    /// light came from the opposite side) and every chunk holding explored pixels is flagged to be
    /// recorded again. Cave layers store height factors rather than directional relief, so they
    /// pass false and keep their shades as they are.
    /// </param>
    public static MapTile Read(Stream source, bool mirrorLegacyHeightShades = true)
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
        if (version is not LegacyPaletteVersion
            and not PaletteOnlyVersion
            and not HeightShadeVersion
            and not SeasonVersion
            and not CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported tile version {version}.");
        }

        var expectedPayloadLength = version switch
        {
            CurrentVersion => CurrentPayloadLength,
            SeasonVersion => SeasonPayloadLength,
            HeightShadeVersion => HeightShadePayloadLength,
            _ => PaletteOnlyPayloadLength,
        };
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

        var heightShades = version >= HeightShadeVersion
            ? payload.AsSpan(
                HeaderLength + MapTile.ExploredByteCount + MapTile.ColorByteCount,
                MapTile.HeightShadeByteCount).ToArray()
            : new byte[MapTile.HeightShadeByteCount];

        // Tiles written before seasons were tracked keep their recorded colours year-round until
        // the player passes by again and the area is re-recorded.
        var seasons = version >= SeasonVersion
            ? payload.AsSpan(SeasonOffset, MapTile.SeasonByteCount).ToArray()
            : new byte[MapTile.SeasonByteCount];

        ushort staleShadeChunks = 0;
        if (version == CurrentVersion)
        {
            staleShadeChunks = BinaryPrimitives.ReadUInt16LittleEndian(
                payload.AsSpan(StaleShadeMaskOffset, StaleShadeMaskLength));
        }
        else if (mirrorLegacyHeightShades)
        {
            // Until 2.8.0 relief was lit from the screen's bottom-right. The tile keeps no
            // heights, so the shades are mirrored around neutral as the closest guess, and each
            // explored chunk is recorded again with the new light when the player is near.
            MirrorLegacyHeightShades(heightShades);
            staleShadeChunks = GetExploredChunks(explored, colors);
        }

        return new MapTile(tileX, tileZ, explored, colors, heightShades, seasons, staleShadeChunks);
    }

    /// <summary>
    /// Approximate conversion of a shade lit from the opposite side: brightness b becomes 2 - b
    /// around neutral (128), so slopes swap their light and dark sides. Unknown (0) stays unknown.
    /// </summary>
    internal static byte MirrorLegacyHeightShade(byte shade) => shade == TerrainHeightShading.Unknown
        ? TerrainHeightShading.Unknown
        : (byte)Math.Clamp((2 * TerrainHeightShading.Neutral) - shade, 1, byte.MaxValue);

    private static void MirrorLegacyHeightShades(byte[] heightShades)
    {
        for (var index = 0; index < heightShades.Length; index++)
        {
            heightShades[index] = MirrorLegacyHeightShade(heightShades[index]);
        }
    }

    private static ushort GetExploredChunks(byte[] explored, byte[] colors)
    {
        var mask = 0;
        for (var pixelIndex = 0; pixelIndex < MapTile.PixelCount; pixelIndex++)
        {
            if (MapTile.TryGetPixelCore(explored, colors, pixelIndex, out _))
            {
                mask |= 1 << MapTile.GetChunkBit(pixelIndex % MapTile.Size, pixelIndex / MapTile.Size);
            }
        }

        return (ushort)mask;
    }
}
