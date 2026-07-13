using System.Numerics;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Network;
using SurvivalcraftTravelMap.Teleport;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class CoordinateTeleportPackageTests
{
    public static TheoryData<CoordinateTeleportMessage> RoundTrips => new()
    {
        CoordinateTeleportMessage.CapabilityRequest(1),
        CoordinateTeleportMessage.CapabilityResponse(2, true, false),
        CoordinateTeleportMessage.SurfaceRequest(3, 123, -456),
        CoordinateTeleportMessage.WaypointRequest(4, new Vector3(1.5f, 64f, -2.5f)),
        CoordinateTeleportMessage.Result(5, CoordinateTeleportResultCode.NoSafePosition, "no safe cell"),
    };

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void Codec_round_trips_every_ID61_message(CoordinateTeleportMessage message)
    {
        Assert.Equal(message, CoordinateTeleportCodec.Deserialize(CoordinateTeleportCodec.Serialize(message)));
        Assert.Equal(61, CoordinateTeleportPackage.PackageId);
    }

    [Theory]
    [InlineData("", "truncated")]
    [InlineData("FF", "kind")]
    [InlineData("0001", "truncated")]
    [InlineData("0401000000FF00", "result")]
    [InlineData("000100000000", "trailing")]
    [InlineData("0201000000010000000000000000", "mode")]
    [InlineData("04010000000101FF", "UTF-8")]
    public void Codec_rejects_malformed_truncated_unknown_or_trailing_payloads(
        string hex,
        string expectedMessage)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => CoordinateTeleportCodec.Deserialize(Convert.FromHexString(hex)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(40000000f)]
    public void Waypoint_requests_reject_non_finite_and_out_of_protocol_coordinates(float invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoordinateTeleportMessage.WaypointRequest(1, new Vector3(invalid, 64f, 0f)));
    }

    [Fact]
    public void Codec_rejects_oversized_result_text()
    {
        Assert.Throws<ArgumentException>(
            () => CoordinateTeleportMessage.Result(1, CoordinateTeleportResultCode.Rejected, new string('x', 257)));
    }

    [Fact]
    public void Decoder_rejects_declared_oversized_text_before_allocating_or_reading_it()
    {
        var payload = Convert.FromHexString("0401000000018102");

        var exception = Assert.Throws<InvalidDataException>(() => CoordinateTeleportCodec.Deserialize(payload));

        Assert.Contains("too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CoordinateTeleportServerSessionTests
{
    [Fact]
    public async Task Server_options_store_defaults_both_modes_on_and_persists_independent_switches()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        var store = new CoordinateTeleportServerOptionsStore(path);

        var defaults = store.Load();
        defaults.SurfaceTeleportEnabled = false;
        store.Save(defaults);
        var reloaded = store.Load();

        Assert.False(reloaded.SurfaceTeleportEnabled);
        Assert.True(reloaded.WaypointTeleportEnabled);
        Assert.True(File.Exists(path));
        await Task.CompletedTask;
    }

    [Fact]
    public void Server_options_store_isolates_a_null_document_and_restores_safe_defaults()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        File.WriteAllText(path, "null");
        var store = new CoordinateTeleportServerOptionsStore(path);

        var restored = store.Load();

        Assert.True(restored.SurfaceTeleportEnabled);
        Assert.True(restored.WaypointTeleportEnabled);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Capability_response_reflects_independent_default_enabled_settings()
    {
        var session = CreateSession(out _);

        var response = await session.HandleAsync(
                "peer-a",
                CoordinateTeleportMessage.CapabilityRequest(1),
                TestContext.Current.CancellationToken);

        Assert.True(response.SurfaceEnabled);
        Assert.True(response.WaypointEnabled);
        Assert.True(new CoordinateTeleportServerOptions().SurfaceTeleportEnabled);
        Assert.True(new CoordinateTeleportServerOptions().WaypointTeleportEnabled);
    }

    [Fact]
    public async Task Surface_request_passes_only_XZ_to_the_server_executor()
    {
        var session = CreateSession(out var executor);

        var response = await session.HandleAsync(
            "peer-a",
            CoordinateTeleportMessage.SurfaceRequest(8, 123, -456),
            CancellationToken.None);

        Assert.Equal((123, -456), executor.SurfaceTarget);
        Assert.Null(executor.WaypointTarget);
        Assert.Equal(CoordinateTeleportResultCode.Success, response.ResultCode);
    }

    [Theory]
    [InlineData(TeleportResult.Success, CoordinateTeleportResultCode.Success)]
    [InlineData(TeleportResult.ChunkTimeout, CoordinateTeleportResultCode.TimedOut)]
    [InlineData(TeleportResult.NoSafePosition, CoordinateTeleportResultCode.NoSafePosition)]
    [InlineData(TeleportResult.OutOfWorld, CoordinateTeleportResultCode.OutOfWorld)]
    [InlineData(TeleportResult.RolledBack, CoordinateTeleportResultCode.RolledBack)]
    public async Task Server_maps_safe_teleport_results_to_stable_codes(
        TeleportResult serviceResult,
        CoordinateTeleportResultCode expected)
    {
        var session = CreateSession(out var executor);
        executor.Result = serviceResult;

        var response = await session.HandleAsync(
            "peer-a",
            CoordinateTeleportMessage.WaypointRequest(9, new Vector3(1.5f, 64f, 2.5f)),
            CancellationToken.None);

        Assert.Equal(expected, response.ResultCode);
    }

    [Fact]
    public async Task Wrong_peer_disabled_mode_and_replayed_ID_never_execute_again()
    {
        var executor = new RecordingExecutor();
        using var session = new CoordinateTeleportServerSession(
            "peer-a",
            executor,
            new CoordinateTeleportServerOptions { SurfaceTeleportEnabled = false });
        var request = CoordinateTeleportMessage.SurfaceRequest(10, 1, 2);

        Assert.Equal(
            CoordinateTeleportResultCode.Rejected,
            (await session.HandleAsync("peer-b", request, CancellationToken.None)).ResultCode);
        Assert.Equal(
            CoordinateTeleportResultCode.Disabled,
            (await session.HandleAsync("peer-a", request, CancellationToken.None)).ResultCode);

        var waypoint = CoordinateTeleportMessage.WaypointRequest(11, new Vector3(1, 2, 3));
        Assert.Equal(CoordinateTeleportResultCode.Success,
            (await session.HandleAsync("peer-a", waypoint, CancellationToken.None)).ResultCode);
        Assert.Equal(CoordinateTeleportResultCode.Duplicate,
            (await session.HandleAsync("peer-a", waypoint, CancellationToken.None)).ResultCode);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Waypoint_mode_can_be_disabled_without_disabling_surface_mode()
    {
        var executor = new RecordingExecutor();
        using var session = new CoordinateTeleportServerSession(
            "peer-a",
            executor,
            new CoordinateTeleportServerOptions { WaypointTeleportEnabled = false });

        Assert.Equal(CoordinateTeleportResultCode.Disabled,
            (await session.HandleAsync(
                "peer-a",
                CoordinateTeleportMessage.WaypointRequest(20, new Vector3(1, 2, 3)),
                CancellationToken.None)).ResultCode);
        Assert.Equal(CoordinateTeleportResultCode.Success,
            (await session.HandleAsync(
                "peer-a",
                CoordinateTeleportMessage.SurfaceRequest(21, 1, 3),
                CancellationToken.None)).ResultCode);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Old_request_IDs_stay_rejected_and_serial_numbers_wrap_from_max_to_one()
    {
        var session = CreateSession(out var executor);
        Assert.Equal(CoordinateTeleportResultCode.Success,
            (await session.HandleAsync("peer-a", CoordinateTeleportMessage.SurfaceRequest(100, 1, 2), CancellationToken.None)).ResultCode);
        Assert.Equal(CoordinateTeleportResultCode.Duplicate,
            (await session.HandleAsync("peer-a", CoordinateTeleportMessage.SurfaceRequest(99, 1, 2), CancellationToken.None)).ResultCode);

        using var wrapping = CreateSession(out var wrappingExecutor);
        Assert.Equal(CoordinateTeleportResultCode.Success,
            (await wrapping.HandleAsync("peer-a", CoordinateTeleportMessage.SurfaceRequest(uint.MaxValue, 1, 2), CancellationToken.None)).ResultCode);
        Assert.Equal(CoordinateTeleportResultCode.Success,
            (await wrapping.HandleAsync("peer-a", CoordinateTeleportMessage.SurfaceRequest(1, 1, 2), CancellationToken.None)).ResultCode);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(2, wrappingExecutor.CallCount);
    }

    [Fact]
    public async Task Concurrent_duplicate_ID_is_rejected_and_disconnect_cancels_the_bound_request()
    {
        var executor = new RecordingExecutor { Block = true };
        using var session = new CoordinateTeleportServerSession(
            "peer-a",
            executor,
            new CoordinateTeleportServerOptions());
        var request = CoordinateTeleportMessage.SurfaceRequest(12, 1, 2);
        var first = session.HandleAsync("peer-a", request, CancellationToken.None);
        await executor.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var duplicate = await session.HandleAsync("peer-a", request, CancellationToken.None);
        session.Disconnect();
        var disconnected = await first;

        Assert.Equal(CoordinateTeleportResultCode.Duplicate, duplicate.ResultCode);
        Assert.Equal(CoordinateTeleportResultCode.Disconnected, disconnected.ResultCode);
        Assert.True(executor.SawCancellation);
    }

    private static CoordinateTeleportServerSession CreateSession(out RecordingExecutor executor)
    {
        executor = new RecordingExecutor();
        return new CoordinateTeleportServerSession(
            "peer-a",
            executor,
            new CoordinateTeleportServerOptions());
    }

    private sealed class RecordingExecutor : ICoordinateTeleportExecutor
    {
        internal (int X, int Z)? SurfaceTarget { get; private set; }
        internal Vector3? WaypointTarget { get; private set; }
        internal TeleportResult Result { get; set; } = TeleportResult.Success;
        internal int CallCount { get; private set; }
        internal bool Block { get; set; }
        internal bool SawCancellation { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TeleportResult> TeleportToSurfaceAsync(int x, int z, CancellationToken cancellationToken)
        {
            SurfaceTarget = (x, z);
            return await ExecuteAsync(cancellationToken);
        }

        public async Task<TeleportResult> TeleportToWaypointAsync(Vector3 xyz, CancellationToken cancellationToken)
        {
            WaypointTarget = xyz;
            return await ExecuteAsync(cancellationToken);
        }

        private async Task<TeleportResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            if (Block)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }

            return Result;
        }
    }
}

public sealed class CoordinateTeleportClientSessionTests
{
    [Fact]
    public async Task Client_waits_for_capability_and_success_without_direct_position_write()
    {
        var clock = new ManualProtocolClock();
        var sent = new List<CoordinateTeleportMessage>();
        var client = new CoordinateTeleportClientSession("server-a", sent.Add, clock, _ => { });

        var pending = client.RequestSurfaceAsync(10, 20, CancellationToken.None);
        var capability = Assert.Single(sent);
        Assert.Equal(CoordinateTeleportMessageKind.CapabilityRequest, capability.Kind);
        Assert.False(pending.IsCompleted);

        Assert.True(client.Receive("server-a", CoordinateTeleportMessage.CapabilityResponse(
            capability.RequestId, true, true)));
        await WaitForCountAsync(sent, 2);
        var request = sent[1];
        Assert.Equal(CoordinateTeleportMessageKind.SurfaceRequest, request.Kind);
        Assert.False(pending.IsCompleted);

        Assert.True(client.Receive("server-a", CoordinateTeleportMessage.Result(
            request.RequestId, CoordinateTeleportResultCode.Success)));
        Assert.Equal(CoordinateTeleportResultCode.Success, await pending);
    }

    [Fact]
    public async Task Capability_timeout_notifies_only_once_per_server_session()
    {
        var clock = new ManualProtocolClock();
        var notifications = new List<string>();
        var client = new CoordinateTeleportClientSession("server-a", _ => { }, clock, notifications.Add);

        var first = client.RequestSurfaceAsync(1, 2, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CoordinateTeleportResultCode.TimedOut, await first);

        var second = client.RequestSurfaceAsync(3, 4, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CoordinateTeleportResultCode.TimedOut, await second);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task Wrong_peer_and_late_response_are_ignored()
    {
        var clock = new ManualProtocolClock();
        var sent = new List<CoordinateTeleportMessage>();
        var client = new CoordinateTeleportClientSession("server-a", sent.Add, clock, _ => { });
        var pending = client.RequestSurfaceAsync(1, 2, CancellationToken.None);
        var capability = Assert.Single(sent);

        Assert.False(client.Receive("server-b", CoordinateTeleportMessage.CapabilityResponse(
            capability.RequestId, true, true)));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CoordinateTeleportResultCode.TimedOut, await pending);
        Assert.False(client.Receive("server-a", CoordinateTeleportMessage.CapabilityResponse(
            capability.RequestId, true, true)));
    }

    [Fact]
    public async Task Late_result_after_request_timeout_is_ignored()
    {
        var clock = new ManualProtocolClock();
        var sent = new List<CoordinateTeleportMessage>();
        var client = new CoordinateTeleportClientSession("server-a", sent.Add, clock, _ => { });
        var pending = client.RequestSurfaceAsync(1, 2, CancellationToken.None);
        var capability = Assert.Single(sent);
        client.Receive("server-a", CoordinateTeleportMessage.CapabilityResponse(
            capability.RequestId, true, true));
        await WaitForCountAsync(sent, 2);
        var request = sent[1];

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(CoordinateTeleportResultCode.TimedOut, await pending);
        Assert.False(client.Receive("server-a", CoordinateTeleportMessage.Result(
            request.RequestId, CoordinateTeleportResultCode.Success)));
    }

    [Fact]
    public async Task Disconnect_cancels_pending_client_request_and_clears_session_state()
    {
        var clock = new ManualProtocolClock();
        var sent = new List<CoordinateTeleportMessage>();
        var client = new CoordinateTeleportClientSession("server-a", sent.Add, clock, _ => { });
        var pending = client.RequestSurfaceAsync(1, 2, CancellationToken.None);
        var capability = Assert.Single(sent);

        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(client.Receive("server-a", CoordinateTeleportMessage.CapabilityResponse(
            capability.RequestId, true, true)));
    }

    [Fact]
    public void Request_ID_sequence_skips_zero_and_wraps_without_reusing_pending_IDs()
    {
        var sequence = new CoordinateTeleportRequestIdSequence(uint.MaxValue - 1);

        Assert.Equal(uint.MaxValue, sequence.Next([]));
        Assert.Equal(1u, sequence.Next([uint.MaxValue]));
        Assert.Equal(2u, sequence.Next([1u]));
    }

    private static async Task WaitForCountAsync<T>(IReadOnlyCollection<T> values, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (values.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(values.Count >= count);
    }

    private sealed class ManualProtocolClock : ICoordinateTeleportProtocolClock
    {
        private readonly List<(TimeSpan Delay, TaskCompletionSource Completion)> _delays = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _delays.Add((delay, completion));
            return completion.Task;
        }

        internal void Advance(TimeSpan duration)
        {
            foreach (var delay in _delays.Where(item => item.Delay <= duration).ToArray())
            {
                delay.Completion.TrySetResult();
                _delays.Remove(delay);
            }
        }
    }
}

public sealed class NetworkAdapterContractTests
{
    [Fact]
    public void Teleport_router_distinguishes_surface_XZ_from_waypoint_XYZ_for_Task9()
    {
        var surface = new TravelMapClientTravelCommand(
            new Vector3(10.5f, 99f, -20.5f),
            TravelMapClientTravelMode.Surface);
        var waypoint = new TravelMapClientTravelCommand(
            new Vector3(10.5f, 42.25f, -20.5f),
            TravelMapClientTravelMode.Waypoint);

        Assert.Equal(TravelMapClientTravelMode.Surface, surface.Mode);
        Assert.Equal(TravelMapClientTravelMode.Waypoint, waypoint.Mode);
        Assert.NotEqual(surface.Mode, waypoint.Mode);
        Assert.Equal(42.25f, waypoint.Target.Y);
    }
}

internal sealed class NetworkTemporaryDirectory : IDisposable
{
    internal NetworkTemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sctm-network-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
