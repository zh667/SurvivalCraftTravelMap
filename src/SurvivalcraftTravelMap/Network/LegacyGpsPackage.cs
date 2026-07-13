using Game.NetWork;
using Newtonsoft.Json;

namespace SurvivalcraftTravelMap.Network;

public enum LegacyGpsMessageKind
{
    Request = 0,
    Response = 1,
    Teleport = 2,
    MultiServerTeleport = 3,
    TeleportResponse = 4,
    TeleportAllow = 5,
}

public sealed record LegacyGpsPlayerData(int ServerNumber, string PlayerName);

public sealed class LegacyGpsMessage : IEquatable<LegacyGpsMessage>
{
    private LegacyGpsMessage(LegacyGpsMessageKind kind)
    {
        Kind = kind;
    }

    public LegacyGpsMessageKind Kind { get; }

    public IReadOnlyList<LegacyGpsPlayerData> Players { get; private init; } = [];

    public string? PlayerName { get; private init; }

    public string? Message { get; private init; }

    public int MessageType { get; private init; }

    public int ServerNumber { get; private init; }

    public bool IsAllowed { get; private init; }

    public static LegacyGpsMessage Request() => new(LegacyGpsMessageKind.Request);

    public static LegacyGpsMessage Response(IReadOnlyList<LegacyGpsPlayerData> players) =>
        new(LegacyGpsMessageKind.Response)
        {
            Players = players?.ToArray() ?? throw new ArgumentNullException(nameof(players)),
        };

    public static LegacyGpsMessage Teleport(string playerGuid) =>
        new(LegacyGpsMessageKind.Teleport) { PlayerName = RequireText(playerGuid, nameof(playerGuid)) };

    public static LegacyGpsMessage MultiServerTeleport(int serverNumber, string playerName) =>
        new(LegacyGpsMessageKind.MultiServerTeleport)
        {
            ServerNumber = serverNumber,
            PlayerName = RequireText(playerName, nameof(playerName)),
        };

    public static LegacyGpsMessage TeleportResponse(int messageType, string message) =>
        new(LegacyGpsMessageKind.TeleportResponse)
        {
            MessageType = messageType,
            Message = message ?? throw new ArgumentNullException(nameof(message)),
        };

    public static LegacyGpsMessage TeleportAllow(bool isAllowed) =>
        new(LegacyGpsMessageKind.TeleportAllow) { IsAllowed = isAllowed };

    public bool Equals(LegacyGpsMessage? other) =>
        other is not null
        && Kind == other.Kind
        && PlayerName == other.PlayerName
        && Message == other.Message
        && MessageType == other.MessageType
        && ServerNumber == other.ServerNumber
        && IsAllowed == other.IsAllowed
        && Players.SequenceEqual(other.Players);

    public override bool Equals(object? obj) => Equals(obj as LegacyGpsMessage);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(PlayerName);
        hash.Add(Message);
        hash.Add(MessageType);
        hash.Add(ServerNumber);
        hash.Add(IsAllowed);
        foreach (var player in Players)
        {
            hash.Add(player);
        }

        return hash.ToHashCode();
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value;
}

public static class LegacyGpsCodec
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxStringBytes = 16 * 1024;
    private const int MaxPlayers = 1024;

    public static byte[] Serialize(LegacyGpsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        Write(writer, message);
        writer.Flush();
        if (stream.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("Legacy GPS payload is too large.");
        }

        return stream.ToArray();
    }

    public static LegacyGpsMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("Legacy GPS payload is too large.");
        }

        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var message = Read(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Legacy GPS payload has trailing bytes.");
        }

        return message;
    }

    internal static void Write(BinaryWriter writer, LegacyGpsMessage message)
    {
        writer.Write((int)message.Kind);
        switch (message.Kind)
        {
            case LegacyGpsMessageKind.Request:
                break;
            case LegacyGpsMessageKind.Response:
                if (message.Players.Count > MaxPlayers)
                {
                    throw new InvalidDataException("Legacy GPS player list is too large.");
                }

                writer.Write(JsonConvert.SerializeObject(message.Players));
                break;
            case LegacyGpsMessageKind.Teleport:
                writer.Write(message.PlayerName!);
                break;
            case LegacyGpsMessageKind.MultiServerTeleport:
                writer.Write(message.ServerNumber);
                writer.Write(message.PlayerName!);
                break;
            case LegacyGpsMessageKind.TeleportResponse:
                writer.Write(message.MessageType);
                writer.Write(message.Message!);
                break;
            case LegacyGpsMessageKind.TeleportAllow:
                writer.Write(message.IsAllowed);
                break;
            default:
                throw new InvalidDataException($"Unknown legacy GPS message kind {(int)message.Kind}.");
        }
    }

    internal static LegacyGpsMessage Read(BinaryReader reader)
    {
        try
        {
            var kindValue = reader.ReadInt32();
            if (!Enum.IsDefined(typeof(LegacyGpsMessageKind), kindValue))
            {
                throw new InvalidDataException($"Unknown legacy GPS message kind {kindValue}.");
            }

            var kind = (LegacyGpsMessageKind)kindValue;
            return kind switch
            {
                LegacyGpsMessageKind.Request => LegacyGpsMessage.Request(),
                LegacyGpsMessageKind.Response => ReadResponse(reader),
                LegacyGpsMessageKind.Teleport => LegacyGpsMessage.Teleport(ReadBoundedString(reader)),
                LegacyGpsMessageKind.MultiServerTeleport => LegacyGpsMessage.MultiServerTeleport(
                    reader.ReadInt32(),
                    ReadBoundedString(reader)),
                LegacyGpsMessageKind.TeleportResponse => LegacyGpsMessage.TeleportResponse(
                    reader.ReadInt32(),
                    ReadBoundedString(reader, allowEmpty: true)),
                LegacyGpsMessageKind.TeleportAllow => LegacyGpsMessage.TeleportAllow(reader.ReadBoolean()),
                _ => throw new InvalidDataException($"Unknown legacy GPS message kind {kindValue}."),
            };
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Legacy GPS payload is truncated.", exception);
        }
    }

    private static LegacyGpsMessage ReadResponse(BinaryReader reader)
    {
        var json = ReadBoundedString(reader, allowEmpty: true);
        List<LegacyGpsPlayerData>? players;
        try
        {
            players = JsonConvert.DeserializeObject<List<LegacyGpsPlayerData>>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Legacy GPS player list is invalid.", exception);
        }

        if (players is null || players.Count > MaxPlayers || players.Any(p => string.IsNullOrWhiteSpace(p.PlayerName)))
        {
            throw new InvalidDataException("Legacy GPS player list is invalid.");
        }

        return LegacyGpsMessage.Response(players);
    }

    private static string ReadBoundedString(BinaryReader reader, bool allowEmpty = false)
    {
        return BoundedBinaryString.Read(
            reader,
            MaxStringBytes,
            "Legacy GPS string",
            allowEmpty);
    }
}

public sealed class LegacyGpsPackage : IPackage
{
    public const byte PackageId = 41;

    public byte ID => PackageId;

    public Client To { get; set; } = null!;

    public Client Except { get; set; } = null!;

    public Client From { get; set; } = null!;

    public ClientState MinNeedState => ClientState.NotConnected;

    public LegacyGpsMessage Message { get; private set; } = LegacyGpsMessage.Request();

    public LegacyGpsPackage()
    {
    }

    public LegacyGpsPackage(LegacyGpsMessage message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public void WriteData(PackageStreamWriter writer) => writer.Write(LegacyGpsCodec.Serialize(Message));

    public void ReadData(PackageStreamReader reader)
    {
        var remaining = checked((int)(reader.BaseStream.Length - reader.BaseStream.Position));
        Message = LegacyGpsCodec.Deserialize(reader.ReadBytes(remaining));
    }

    public void Handle(ProjectNet projectNet, NetNode netNode, bool isServer) =>
        TravelMapNetworkRuntime.HandleLegacy(this, projectNet, netNode, isServer);
}
