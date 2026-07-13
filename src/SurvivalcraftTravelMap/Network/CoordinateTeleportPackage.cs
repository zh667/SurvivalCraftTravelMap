using System.Numerics;
using System.Text;
using Game.NetWork;
using SurvivalcraftTravelMap.Teleport;

namespace SurvivalcraftTravelMap.Network;

public enum CoordinateTeleportMessageKind : byte
{
    CapabilityRequest = 0,
    CapabilityResponse = 1,
    SurfaceRequest = 2,
    WaypointRequest = 3,
    Result = 4,
}

public enum CoordinateTeleportMode : byte
{
    Surface = 0,
    Waypoint = 1,
}

public enum CoordinateTeleportResultCode : byte
{
    Success = 0,
    Rejected = 1,
    Unsupported = 2,
    Disabled = 3,
    TimedOut = 4,
    NoSafePosition = 5,
    OutOfWorld = 6,
    RolledBack = 7,
    Malformed = 8,
    Duplicate = 9,
    Disconnected = 10,
    InternalError = 11,
}

public sealed record CoordinateTeleportMessage
{
    public const float MaxCoordinateMagnitude = 30_000_000f;
    public const int MaxResultTextUtf8Bytes = 256;

    private CoordinateTeleportMessage(CoordinateTeleportMessageKind kind, uint requestId)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId), "Request ID zero is reserved.");
        }

        Kind = kind;
        RequestId = requestId;
    }

    public CoordinateTeleportMessageKind Kind { get; }

    public uint RequestId { get; }

    public CoordinateTeleportMode? Mode { get; private init; }

    public int X { get; private init; }

    public int Z { get; private init; }

    public Vector3 Target { get; private init; }

    public bool SurfaceEnabled { get; private init; }

    public bool WaypointEnabled { get; private init; }

    public CoordinateTeleportResultCode? ResultCode { get; private init; }

    public string ResultText { get; private init; } = string.Empty;

    public static CoordinateTeleportMessage CapabilityRequest(uint requestId) =>
        new(CoordinateTeleportMessageKind.CapabilityRequest, requestId);

    public static CoordinateTeleportMessage CapabilityResponse(
        uint requestId,
        bool surfaceEnabled,
        bool waypointEnabled) =>
        new(CoordinateTeleportMessageKind.CapabilityResponse, requestId)
        {
            SurfaceEnabled = surfaceEnabled,
            WaypointEnabled = waypointEnabled,
        };

    public static CoordinateTeleportMessage SurfaceRequest(uint requestId, int x, int z)
    {
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(z, nameof(z));
        return new CoordinateTeleportMessage(CoordinateTeleportMessageKind.SurfaceRequest, requestId)
        {
            Mode = CoordinateTeleportMode.Surface,
            X = x,
            Z = z,
        };
    }

    public static CoordinateTeleportMessage WaypointRequest(uint requestId, Vector3 target)
    {
        ValidateCoordinate(target.X, nameof(target));
        ValidateCoordinate(target.Y, nameof(target));
        ValidateCoordinate(target.Z, nameof(target));
        return new CoordinateTeleportMessage(CoordinateTeleportMessageKind.WaypointRequest, requestId)
        {
            Mode = CoordinateTeleportMode.Waypoint,
            Target = target,
        };
    }

    public static CoordinateTeleportMessage Result(
        uint requestId,
        CoordinateTeleportResultCode resultCode,
        string? resultText = null)
    {
        if (!Enum.IsDefined(resultCode))
        {
            throw new ArgumentOutOfRangeException(nameof(resultCode));
        }

        resultText ??= string.Empty;
        if (Encoding.UTF8.GetByteCount(resultText) > MaxResultTextUtf8Bytes)
        {
            throw new ArgumentException("Result text exceeds 256 UTF-8 bytes.", nameof(resultText));
        }

        return new CoordinateTeleportMessage(CoordinateTeleportMessageKind.Result, requestId)
        {
            ResultCode = resultCode,
            ResultText = resultText,
        };
    }

    private static void ValidateCoordinate(float coordinate, string parameterName)
    {
        if (!float.IsFinite(coordinate) || MathF.Abs(coordinate) > MaxCoordinateMagnitude)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Coordinate is not finite or exceeds protocol bounds.");
        }
    }

    private static void ValidateCoordinate(int coordinate, string parameterName)
    {
        if (Math.Abs((long)coordinate) > (long)MaxCoordinateMagnitude)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Coordinate exceeds protocol bounds.");
        }
    }
}

public static class CoordinateTeleportCodec
{
    private const int MaxPayloadBytes = 512;

    public static byte[] Serialize(CoordinateTeleportMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(writer, message);
        writer.Flush();
        if (stream.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("Coordinate teleport payload is too large.");
        }

        return stream.ToArray();
    }

    public static CoordinateTeleportMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("Coordinate teleport payload is too large.");
        }

        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        CoordinateTeleportMessage message;
        try
        {
            message = Read(reader);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("Coordinate teleport payload is truncated or invalid.", exception);
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Coordinate teleport payload has trailing bytes.");
        }

        return message;
    }

    internal static void Write(BinaryWriter writer, CoordinateTeleportMessage message)
    {
        writer.Write((byte)message.Kind);
        writer.Write(message.RequestId);
        switch (message.Kind)
        {
            case CoordinateTeleportMessageKind.CapabilityRequest:
                break;
            case CoordinateTeleportMessageKind.CapabilityResponse:
                writer.Write((byte)((message.SurfaceEnabled ? 1 : 0) | (message.WaypointEnabled ? 2 : 0)));
                break;
            case CoordinateTeleportMessageKind.SurfaceRequest:
                writer.Write((byte)CoordinateTeleportMode.Surface);
                writer.Write(message.X);
                writer.Write(message.Z);
                break;
            case CoordinateTeleportMessageKind.WaypointRequest:
                writer.Write((byte)CoordinateTeleportMode.Waypoint);
                writer.Write(message.Target.X);
                writer.Write(message.Target.Y);
                writer.Write(message.Target.Z);
                break;
            case CoordinateTeleportMessageKind.Result:
                writer.Write((byte)message.ResultCode!.Value);
                writer.Write(message.ResultText);
                break;
            default:
                throw new InvalidDataException($"Unknown coordinate teleport message kind {(byte)message.Kind}.");
        }
    }

    internal static CoordinateTeleportMessage Read(BinaryReader reader)
    {
        var kindValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(CoordinateTeleportMessageKind), kindValue))
        {
            throw new InvalidDataException($"Unknown coordinate teleport message kind {kindValue}.");
        }

        var requestId = reader.ReadUInt32();
        if (requestId == 0)
        {
            throw new InvalidDataException("Coordinate teleport request ID zero is reserved.");
        }

        return (CoordinateTeleportMessageKind)kindValue switch
        {
            CoordinateTeleportMessageKind.CapabilityRequest =>
                CoordinateTeleportMessage.CapabilityRequest(requestId),
            CoordinateTeleportMessageKind.CapabilityResponse => ReadCapabilityResponse(reader, requestId),
            CoordinateTeleportMessageKind.SurfaceRequest => ReadSurfaceRequest(reader, requestId),
            CoordinateTeleportMessageKind.WaypointRequest => ReadWaypointRequest(reader, requestId),
            CoordinateTeleportMessageKind.Result => ReadResult(reader, requestId),
            _ => throw new InvalidDataException($"Unknown coordinate teleport message kind {kindValue}."),
        };
    }

    private static CoordinateTeleportMessage ReadCapabilityResponse(BinaryReader reader, uint requestId)
    {
        var flags = reader.ReadByte();
        if ((flags & ~3) != 0)
        {
            throw new InvalidDataException("Coordinate teleport capability flags are invalid.");
        }

        return CoordinateTeleportMessage.CapabilityResponse(
            requestId,
            (flags & 1) != 0,
            (flags & 2) != 0);
    }

    private static CoordinateTeleportMessage ReadSurfaceRequest(BinaryReader reader, uint requestId)
    {
        var mode = reader.ReadByte();
        if (mode != (byte)CoordinateTeleportMode.Surface)
        {
            throw new InvalidDataException("Coordinate teleport request mode does not match surface payload.");
        }

        return CoordinateTeleportMessage.SurfaceRequest(requestId, reader.ReadInt32(), reader.ReadInt32());
    }

    private static CoordinateTeleportMessage ReadWaypointRequest(BinaryReader reader, uint requestId)
    {
        var mode = reader.ReadByte();
        if (mode != (byte)CoordinateTeleportMode.Waypoint)
        {
            throw new InvalidDataException("Coordinate teleport request mode does not match waypoint payload.");
        }

        return CoordinateTeleportMessage.WaypointRequest(
            requestId,
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
    }

    private static CoordinateTeleportMessage ReadResult(BinaryReader reader, uint requestId)
    {
        var resultValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(CoordinateTeleportResultCode), resultValue))
        {
            throw new InvalidDataException($"Unknown coordinate teleport result code {resultValue}.");
        }

        var text = BoundedBinaryString.Read(
            reader,
            CoordinateTeleportMessage.MaxResultTextUtf8Bytes,
            "Coordinate teleport result text",
            allowEmpty: true);

        return CoordinateTeleportMessage.Result(requestId, (CoordinateTeleportResultCode)resultValue, text);
    }
}

internal static class BoundedBinaryString
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string Read(
        BinaryReader reader,
        int maxUtf8Bytes,
        string description,
        bool allowEmpty)
    {
        int byteLength;
        try
        {
            byteLength = reader.Read7BitEncodedInt();
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{description} length is invalid.", exception);
        }

        if (byteLength < 0 || byteLength > maxUtf8Bytes)
        {
            throw new InvalidDataException($"{description} is too large.");
        }

        var bytes = reader.ReadBytes(byteLength);
        if (bytes.Length != byteLength)
        {
            throw new EndOfStreamException($"{description} is truncated.");
        }

        string value;
        try
        {
            value = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{description} is not valid UTF-8.", exception);
        }

        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{description} cannot be empty.");
        }

        return value;
    }
}

public sealed class CoordinateTeleportPackage : IPackage
{
    public const byte PackageId = 61;

    public byte ID => PackageId;

    public Client To { get; set; } = null!;

    public Client Except { get; set; } = null!;

    public Client From { get; set; } = null!;

    public ClientState MinNeedState => ClientState.NotConnected;

    public CoordinateTeleportMessage Message { get; private set; } =
        CoordinateTeleportMessage.CapabilityRequest(1);

    public CoordinateTeleportPackage()
    {
    }

    public CoordinateTeleportPackage(CoordinateTeleportMessage message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public void WriteData(PackageStreamWriter writer) => writer.Write(CoordinateTeleportCodec.Serialize(Message));

    public void ReadData(PackageStreamReader reader)
    {
        var remaining = checked((int)(reader.BaseStream.Length - reader.BaseStream.Position));
        Message = CoordinateTeleportCodec.Deserialize(reader.ReadBytes(remaining));
    }

    public void Handle(ProjectNet projectNet, NetNode netNode, bool isServer) =>
        TravelMapNetworkRuntime.HandleCoordinate(this, projectNet, netNode, isServer);
}

public sealed class CoordinateTeleportServerOptions
{
    public bool SurfaceTeleportEnabled { get; set; } = true;

    public bool WaypointTeleportEnabled { get; set; } = true;
}

public interface ICoordinateTeleportExecutor
{
    Task<TeleportResult> TeleportToSurfaceAsync(int x, int z, CancellationToken cancellationToken);

    Task<TeleportResult> TeleportToWaypointAsync(Vector3 xyz, CancellationToken cancellationToken);
}

public sealed class SafeTeleportExecutor(SafeTeleportService service) : ICoordinateTeleportExecutor
{
    private readonly SafeTeleportService _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task<TeleportResult> TeleportToSurfaceAsync(int x, int z, CancellationToken cancellationToken) =>
        _service.TeleportToSurfaceAsync(x, z, cancellationToken);

    public Task<TeleportResult> TeleportToWaypointAsync(Vector3 xyz, CancellationToken cancellationToken) =>
        _service.TeleportToWaypointAsync(xyz, cancellationToken);
}

public sealed class CoordinateTeleportServerSession : IDisposable
{
    private const int ReplayWindow = 4096;

    private readonly object _sync = new();
    private readonly string _peerId;
    private readonly ICoordinateTeleportExecutor _executor;
    private readonly CoordinateTeleportServerOptions _options;
    private readonly CancellationTokenSource _disconnect = new();
    private readonly HashSet<uint> _inFlight = [];
    private readonly HashSet<uint> _completed = [];
    private readonly Queue<uint> _completedOrder = [];
    private uint _lastAcceptedRequestId;
    private bool _hasLastAcceptedRequestId;
    private bool _disposed;

    public CoordinateTeleportServerSession(
        string peerId,
        ICoordinateTeleportExecutor executor,
        CoordinateTeleportServerOptions options)
    {
        _peerId = string.IsNullOrWhiteSpace(peerId)
            ? throw new ArgumentException("Peer ID is required.", nameof(peerId))
            : peerId;
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<CoordinateTeleportMessage> HandleAsync(
        string senderPeerId,
        CoordinateTeleportMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(_peerId, senderPeerId, StringComparison.Ordinal))
        {
            return CoordinateTeleportMessage.Result(message.RequestId, CoordinateTeleportResultCode.Rejected);
        }

        if (message.Kind == CoordinateTeleportMessageKind.CapabilityRequest)
        {
            lock (_sync)
            {
                return _disposed
                    ? CoordinateTeleportMessage.Result(message.RequestId, CoordinateTeleportResultCode.Disconnected)
                    : CoordinateTeleportMessage.CapabilityResponse(
                        message.RequestId,
                        _options.SurfaceTeleportEnabled,
                        _options.WaypointTeleportEnabled);
            }
        }

        if (message.Kind is not (CoordinateTeleportMessageKind.SurfaceRequest
            or CoordinateTeleportMessageKind.WaypointRequest))
        {
            return CoordinateTeleportMessage.Result(message.RequestId, CoordinateTeleportResultCode.Malformed);
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return CoordinateTeleportMessage.Result(message.RequestId, CoordinateTeleportResultCode.Disconnected);
            }

            if (_inFlight.Contains(message.RequestId) || _completed.Contains(message.RequestId))
            {
                return CoordinateTeleportMessage.Result(message.RequestId, CoordinateTeleportResultCode.Duplicate);
            }

            if (_hasLastAcceptedRequestId
                && !IsNewerRequestId(message.RequestId, _lastAcceptedRequestId))
            {
                return CoordinateTeleportMessage.Result(message.RequestId, CoordinateTeleportResultCode.Duplicate);
            }

            _inFlight.Add(message.RequestId);
            _lastAcceptedRequestId = message.RequestId;
            _hasLastAcceptedRequestId = true;
        }

        var result = CoordinateTeleportResultCode.InternalError;
        try
        {
            if ((message.Kind == CoordinateTeleportMessageKind.SurfaceRequest
                    && !_options.SurfaceTeleportEnabled)
                || (message.Kind == CoordinateTeleportMessageKind.WaypointRequest
                    && !_options.WaypointTeleportEnabled))
            {
                result = CoordinateTeleportResultCode.Disabled;
            }
            else
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _disconnect.Token);
                var serviceResult = message.Kind == CoordinateTeleportMessageKind.SurfaceRequest
                    ? await _executor.TeleportToSurfaceAsync(message.X, message.Z, linked.Token).ConfigureAwait(false)
                    : await _executor.TeleportToWaypointAsync(message.Target, linked.Token).ConfigureAwait(false);
                result = MapResult(serviceResult);
            }
        }
        catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
        {
            result = CoordinateTeleportResultCode.Disconnected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = CoordinateTeleportResultCode.TimedOut;
        }
        catch
        {
            result = CoordinateTeleportResultCode.InternalError;
        }
        finally
        {
            lock (_sync)
            {
                _inFlight.Remove(message.RequestId);
                if (!_disposed)
                {
                    RememberCompleted(message.RequestId);
                }
            }
        }

        return CoordinateTeleportMessage.Result(message.RequestId, result);
    }

    public void Disconnect() => Dispose();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _completed.Clear();
            _completedOrder.Clear();
            _disconnect.Cancel();
        }
    }

    private void RememberCompleted(uint requestId)
    {
        if (!_completed.Add(requestId))
        {
            return;
        }

        _completedOrder.Enqueue(requestId);
        while (_completedOrder.Count > ReplayWindow)
        {
            _completed.Remove(_completedOrder.Dequeue());
        }
    }

    private static CoordinateTeleportResultCode MapResult(TeleportResult result) => result switch
    {
        TeleportResult.Success => CoordinateTeleportResultCode.Success,
        TeleportResult.ChunkTimeout => CoordinateTeleportResultCode.TimedOut,
        TeleportResult.NoSafePosition => CoordinateTeleportResultCode.NoSafePosition,
        TeleportResult.OutOfWorld => CoordinateTeleportResultCode.OutOfWorld,
        TeleportResult.RolledBack => CoordinateTeleportResultCode.RolledBack,
        _ => CoordinateTeleportResultCode.InternalError,
    };

    private static bool IsNewerRequestId(uint candidate, uint previous) =>
        unchecked((int)(candidate - previous)) > 0;
}

public interface ICoordinateTeleportProtocolClock
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemCoordinateTeleportProtocolClock : ICoordinateTeleportProtocolClock
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class CoordinateTeleportRequestIdSequence
{
    private uint _current;

    public CoordinateTeleportRequestIdSequence(uint initialValue = 0)
    {
        _current = initialValue;
    }

    public uint Next(IReadOnlyCollection<uint> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        for (ulong attempts = 0; attempts < uint.MaxValue; attempts++)
        {
            _current = unchecked(_current + 1);
            if (_current != 0 && !inUse.Contains(_current))
            {
                return _current;
            }
        }

        throw new InvalidOperationException("No coordinate teleport request IDs are available.");
    }
}

public sealed class CoordinateTeleportClientSession : IDisposable
{
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    public const string UnsupportedOrTimeoutMessage = "服务器不支持地图传送或请求超时";

    private readonly object _sync = new();
    private readonly string _serverPeerId;
    private readonly Action<CoordinateTeleportMessage> _send;
    private readonly ICoordinateTeleportProtocolClock _clock;
    private readonly Action<string> _notify;
    private readonly CoordinateTeleportRequestIdSequence _ids;
    private readonly Dictionary<uint, PendingResponse> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private (bool Surface, bool Waypoint)? _capabilities;
    private bool _notifiedUnsupportedOrTimeout;
    private bool _disposed;

    public CoordinateTeleportClientSession(
        string serverPeerId,
        Action<CoordinateTeleportMessage> send,
        ICoordinateTeleportProtocolClock clock,
        Action<string> notify,
        uint initialRequestId = 0)
    {
        _serverPeerId = string.IsNullOrWhiteSpace(serverPeerId)
            ? throw new ArgumentException("Server peer ID is required.", nameof(serverPeerId))
            : serverPeerId;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _ids = new CoordinateTeleportRequestIdSequence(initialRequestId);
    }

    public Task<CoordinateTeleportResultCode> RequestSurfaceAsync(
        int x,
        int z,
        CancellationToken cancellationToken) =>
        RequestAsync(CoordinateTeleportMode.Surface, new Vector3(x, 0f, z), cancellationToken);

    public Task<CoordinateTeleportResultCode> RequestWaypointAsync(
        Vector3 target,
        CancellationToken cancellationToken) =>
        RequestAsync(CoordinateTeleportMode.Waypoint, target, cancellationToken);

    public bool Receive(string senderPeerId, CoordinateTeleportMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        PendingResponse? pending;
        lock (_sync)
        {
            if (_disposed
                || !string.Equals(_serverPeerId, senderPeerId, StringComparison.Ordinal)
                || !_pending.TryGetValue(message.RequestId, out pending)
                || message.Kind != pending.ExpectedKind)
            {
                return false;
            }

            _pending.Remove(message.RequestId);
            if (message.Kind == CoordinateTeleportMessageKind.CapabilityResponse)
            {
                _capabilities = (message.SurfaceEnabled, message.WaypointEnabled);
            }
        }

        pending.Completion.TrySetResult(message);
        return true;
    }

    public void Dispose()
    {
        PendingResponse[] pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            pending = _pending.Values.ToArray();
            _pending.Clear();
            _capabilities = null;
        }

        foreach (var response in pending)
        {
            response.Completion.TrySetCanceled(_lifetime.Token);
        }
    }

    private async Task<CoordinateTeleportResultCode> RequestAsync(
        CoordinateTeleportMode mode,
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var capabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.HasValue)
        {
            return CoordinateTeleportResultCode.TimedOut;
        }

        if ((mode == CoordinateTeleportMode.Surface && !capabilities.Value.Surface)
            || (mode == CoordinateTeleportMode.Waypoint && !capabilities.Value.Waypoint))
        {
            NotifyUnsupportedOrTimeoutOnce();
            return CoordinateTeleportResultCode.Unsupported;
        }

        uint requestId;
        CoordinateTeleportMessage request;
        lock (_sync)
        {
            ThrowIfDisposed();
            requestId = _ids.Next(_pending.Keys);
            request = mode == CoordinateTeleportMode.Surface
                ? CoordinateTeleportMessage.SurfaceRequest(
                    requestId,
                    checked((int)target.X),
                    checked((int)target.Z))
                : CoordinateTeleportMessage.WaypointRequest(requestId, target);
        }

        var response = await SendAndWaitAsync(
            request,
            CoordinateTeleportMessageKind.Result,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            NotifyUnsupportedOrTimeoutOnce();
            return CoordinateTeleportResultCode.TimedOut;
        }

        return response.ResultCode!.Value;
    }

    private async Task<(bool Surface, bool Waypoint)?> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_capabilities.HasValue)
            {
                return _capabilities;
            }
        }

        uint requestId;
        lock (_sync)
        {
            requestId = _ids.Next(_pending.Keys);
        }

        var response = await SendAndWaitAsync(
            CoordinateTeleportMessage.CapabilityRequest(requestId),
            CoordinateTeleportMessageKind.CapabilityResponse,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            NotifyUnsupportedOrTimeoutOnce();
            return null;
        }

        return (response.SurfaceEnabled, response.WaypointEnabled);
    }

    private async Task<CoordinateTeleportMessage?> SendAndWaitAsync(
        CoordinateTeleportMessage request,
        CoordinateTeleportMessageKind expectedKind,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CoordinateTeleportMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ThrowIfDisposed();
            _pending.Add(request.RequestId, new PendingResponse(expectedKind, completion));
        }

        try
        {
            _send(request);
        }
        catch
        {
            RemovePending(request.RequestId);
            throw;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var timeout = _clock.DelayAsync(ResponseTimeout, linked.Token);
        var completed = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
        if (completed == completion.Task)
        {
            linked.Cancel();
            ObserveCancellation(timeout);
            return await completion.Task.ConfigureAwait(false);
        }

        RemovePending(request.RequestId);
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException(_lifetime.Token);
        }

        await timeout.ConfigureAwait(false);
        return null;
    }

    private void RemovePending(uint requestId)
    {
        lock (_sync)
        {
            _pending.Remove(requestId);
        }
    }

    private void NotifyUnsupportedOrTimeoutOnce()
    {
        lock (_sync)
        {
            if (_notifiedUnsupportedOrTimeout)
            {
                return;
            }

            _notifiedUnsupportedOrTimeout = true;
        }

        _notify(UnsupportedOrTimeoutMessage);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CoordinateTeleportClientSession));
        }
    }

    private static void ObserveCancellation(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record PendingResponse(
        CoordinateTeleportMessageKind ExpectedKind,
        TaskCompletionSource<CoordinateTeleportMessage> Completion);
}
