using System.Numerics;
using Game.NetWork;
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

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void Network_ReadData_stops_at_the_next_package_sentinel(
        CoordinateTeleportMessage message)
    {
        using var writer = new PackageStreamWriter();
        writer.Write(CoordinateTeleportCodec.Serialize(message));
        writer.Write((byte)0x88);
        writer.Write((byte)77);
        using var reader = new PackageStreamReader(writer.Data());
        var package = new CoordinateTeleportPackage();

        package.ReadData(reader);

        Assert.Equal(message, package.Message);
        Assert.Equal(0x88, reader.ReadByte());
        Assert.Equal(77, reader.ReadByte());
    }

    [Fact]
    public void Network_ReadData_rejects_oversized_string_length_before_consuming_following_bytes()
    {
        using var writer = new PackageStreamWriter();
        writer.Write(Convert.FromHexString("0401000000018102"));
        writer.Write((byte)0x88);
        using var reader = new PackageStreamReader(writer.Data());
        var package = new CoordinateTeleportPackage();

        Assert.Throws<InvalidDataException>(() => package.ReadData(reader));
        Assert.Equal(8, reader.BaseStream.Position);
        Assert.Equal(0x88, reader.ReadByte());
    }

    [Fact]
    public void Network_ReadData_rejects_a_truncated_fixed_payload()
    {
        using var writer = new PackageStreamWriter();
        writer.Write(Convert.FromHexString("020100000000"));
        using var reader = new PackageStreamReader(writer.Data());

        Assert.Throws<InvalidDataException>(() => new CoordinateTeleportPackage().ReadData(reader));
    }
}

public sealed class CoordinateTeleportPackageTestsServerSession
{
    [Fact]
    public void Server_execution_deadline_is_strictly_shorter_than_client_response_timeout()
    {
        Assert.True(CoordinateTeleportServerSession.ExecutionDeadline < CoordinateTeleportClientSession.ResponseTimeout);
    }

    [Fact]
    public async Task Unexpected_remote_executor_failure_is_diagnosed_and_mapped_to_internal_error()
    {
        var executor = new ThrowingExecutor(new InvalidOperationException("remote sentinel 741852"));
        using var session = new CoordinateTeleportServerSession(
            "peer-a",
            executor,
            new CoordinateTeleportServerOptions());
        var request = CoordinateTeleportMessage.SurfaceRequest(37, 123, -456);
        var diagnosticContext = new TeleportRequestDiagnosticContext(
            "remote",
            request.RequestId,
            request.Kind.ToString());

        CoordinateTeleportMessage response;
        using (TeleportDiagnosticContext.Ensure(diagnosticContext))
        {
            response = await session.HandleAsync(
                "peer-a",
                request,
                TestContext.Current.CancellationToken);
            Assert.True(TeleportDiagnosticContext.HasReportedFailure);
        }

        Assert.Equal(CoordinateTeleportResultCode.InternalError, response.ResultCode);
        Assert.Equal(diagnosticContext, executor.ObservedContext);
        Assert.False(executor.ObservedReportedFailure);
    }

    [Fact]
    public async Task Server_deadline_cancels_slow_execution_returns_timeout_and_releases_session()
    {
        var executor = new RecordingExecutor { Block = true };
        using var session = new CoordinateTeleportServerSession(
            "peer-a",
            executor,
            new CoordinateTeleportServerOptions(),
            executionDeadline: TimeSpan.FromMilliseconds(20));

        var timedOut = await session.HandleAsync(
            "peer-a",
            CoordinateTeleportMessage.SurfaceRequest(1, 10, 20),
            TestContext.Current.CancellationToken);
        executor.Block = false;
        var next = await session.HandleAsync(
            "peer-a",
            CoordinateTeleportMessage.SurfaceRequest(2, 11, 21),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.TimedOut, timedOut.ResultCode);
        Assert.True(executor.SawCancellation);
        Assert.Equal(CoordinateTeleportResultCode.Success, next.ResultCode);
        Assert.Equal(1, executor.SuccessfulExecutionCount);
    }

    [Fact]
    public async Task Different_ID61_request_ids_cannot_overlap_one_players_safe_teleport_transaction()
    {
        var context = new TeleportTestContext();
        var service = context.Service;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Terrain.SetSafeFeet(0, 65, 0);
        context.Terrain.SetSafeFeet(1, 65, 0);
        context.Clock.WaitForUpdate = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        using var session = new CoordinateTeleportServerSession(
            "peer-a",
            new SafeTeleportExecutor(service),
            new CoordinateTeleportServerOptions());

        var first = session.HandleAsync(
            "peer-a",
            CoordinateTeleportMessage.WaypointRequest(12, new Vector3(0f, 65f, 0f)),
            CancellationToken.None);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var overlapping = await session.HandleAsync(
            "peer-a",
            CoordinateTeleportMessage.SurfaceRequest(13, 1, 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Rejected, overlapping.ResultCode);
        Assert.Single(context.Mover.Movements);
        release.TrySetResult();
        Assert.Equal(CoordinateTeleportResultCode.Success, (await first).ResultCode);
    }

    [Fact]
    public void Future_server_settings_schema_is_read_only_and_never_overwritten()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        const string future = """
            {"schemaVersion":2,"surfaceTeleportEnabled":false,"futureSetting":"keep"}
            """;
        File.WriteAllText(path, future);
        var store = new CoordinateTeleportServerOptionsStore(path);

        var loaded = store.LoadWithOutcome();

        Assert.Equal(
            CoordinateTeleportServerOptionsLoadOutcome.UnsupportedFutureSchemaReadOnly,
            loaded.Outcome);
        Assert.True(loaded.Options.SurfaceTeleportEnabled);
        Assert.True(loaded.Options.WaypointTeleportEnabled);
        Assert.Throws<InvalidOperationException>(() => store.Save(new CoordinateTeleportServerOptions()));
        Assert.Equal(future, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Arbitrarily_large_future_schema_is_preserved_read_only()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        const string future = """
            {"schemaVersion":999999999999999999999999999,"futureSetting":"keep"}
            """;
        File.WriteAllText(path, future);
        var store = new CoordinateTeleportServerOptionsStore(path);

        var loaded = store.LoadWithOutcome();

        Assert.Equal(
            CoordinateTeleportServerOptionsLoadOutcome.UnsupportedFutureSchemaReadOnly,
            loaded.Outcome);
        Assert.Equal(future, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Future_schema_runtime_warning_is_emitted_once()
    {
        var gate = new CoordinateTeleportFutureSchemaWarningGate();
        var messages = new List<string>();
        var result = new CoordinateTeleportServerOptionsLoadResult(
            new CoordinateTeleportServerOptions(),
            CoordinateTeleportServerOptionsLoadOutcome.UnsupportedFutureSchemaReadOnly);

        gate.NotifyIfNeeded(result, messages.Add);
        gate.NotifyIfNeeded(result, messages.Add);

        Assert.Single(messages);
        Assert.Contains("read-only", messages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":0}")]
    [InlineData("null")]
    [InlineData("{")]
    public void Invalid_server_settings_are_isolated_and_recreated_as_schema_one(string json)
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        File.WriteAllText(path, json);
        var store = new CoordinateTeleportServerOptionsStore(path);

        var loaded = store.LoadWithOutcome();

        Assert.Equal(CoordinateTeleportServerOptionsLoadOutcome.CorruptIsolated, loaded.Outcome);
        Assert.True(loaded.Options.SurfaceTeleportEnabled);
        Assert.True(loaded.Options.WaypointTeleportEnabled);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_one_allows_unknown_fields_and_null_switches_as_safe_defaults()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        File.WriteAllText(
            path,
            """
            {"schemaVersion":1,"surfaceTeleportEnabled":null,"waypointTeleportEnabled":null,"unknown":"kept-compatible"}
            """);
        var store = new CoordinateTeleportServerOptionsStore(path);

        var loaded = store.LoadWithOutcome();

        Assert.Equal(CoordinateTeleportServerOptionsLoadOutcome.Loaded, loaded.Outcome);
        Assert.True(loaded.Options.SurfaceTeleportEnabled);
        Assert.True(loaded.Options.WaypointTeleportEnabled);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Missing_settings_are_created_and_a_stale_tmp_is_replaced_atomically()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        var store = new CoordinateTeleportServerOptionsStore(path);

        var created = store.LoadWithOutcome();
        File.WriteAllText(path + ".tmp", "stale");
        store.Save(new CoordinateTeleportServerOptions
        {
            SurfaceTeleportEnabled = false,
            WaypointTeleportEnabled = true,
        });

        Assert.Equal(CoordinateTeleportServerOptionsLoadOutcome.Created, created.Outcome);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(store.Load().SurfaceTeleportEnabled);
    }

    [Fact]
    public void Failed_temporary_write_leaves_the_original_settings_intact()
    {
        using var directory = new NetworkTemporaryDirectory();
        var path = Path.Combine(directory.Path, "server-settings.json");
        var store = new CoordinateTeleportServerOptionsStore(path);
        store.Save(new CoordinateTeleportServerOptions
        {
            SurfaceTeleportEnabled = false,
            WaypointTeleportEnabled = true,
        });
        var original = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => store.Save(new CoordinateTeleportServerOptions()));
        Assert.Equal(original, File.ReadAllText(path));
    }

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
    [InlineData(TeleportResult.Busy, CoordinateTeleportResultCode.Rejected)]
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
        internal int SuccessfulExecutionCount { get; private set; }
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

            SuccessfulExecutionCount++;
            return Result;
        }
    }

    private sealed class ThrowingExecutor(Exception failure) : ICoordinateTeleportExecutor
    {
        internal TeleportRequestDiagnosticContext? ObservedContext { get; private set; }

        internal bool ObservedReportedFailure { get; private set; }

        public Task<TeleportResult> TeleportToSurfaceAsync(
            int x,
            int z,
            CancellationToken cancellationToken) => Throw();

        public Task<TeleportResult> TeleportToWaypointAsync(
            Vector3 xyz,
            CancellationToken cancellationToken) => Throw();

        private Task<TeleportResult> Throw()
        {
            ObservedContext = TeleportDiagnosticContext.Current;
            ObservedReportedFailure = TeleportDiagnosticContext.HasReportedFailure;
            return Task.FromException<TeleportResult>(failure);
        }
    }
}

public sealed class CoordinateTeleportPackageTestsClientSession
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

    [Fact]
    public async Task Unexpected_host_executor_failure_reports_original_request_and_internal_error()
    {
        var executor = new HostRecordingExecutor
        {
            Failure = new InvalidOperationException("host sentinel 963258"),
        };
        var reported = new List<(CoordinateTeleportMessage Message, CoordinateTeleportResultCode Result)>();
        using var host = CreateObservedHost(
            executor,
            (message, result) => reported.Add((message, result)));
        var diagnosticContext = new TeleportRequestDiagnosticContext(
            "host",
            1,
            CoordinateTeleportMessageKind.SurfaceRequest.ToString());

        CoordinateTeleportResultCode result;
        using (TeleportDiagnosticContext.Ensure(diagnosticContext))
        {
            result = await host.RequestSurfaceAsync(
                10,
                20,
                TestContext.Current.CancellationToken);
            Assert.True(TeleportDiagnosticContext.HasReportedFailure);
        }

        Assert.Equal(CoordinateTeleportResultCode.InternalError, result);
        Assert.Equal(diagnosticContext, executor.ObservedContext);
        var observation = Assert.Single(reported);
        Assert.Equal(1u, observation.Message.RequestId);
        Assert.Equal(CoordinateTeleportMessageKind.SurfaceRequest, observation.Message.Kind);
        Assert.Equal(CoordinateTeleportResultCode.InternalError, observation.Result);
        Assert.DoesNotContain(reported, item => item.Result == CoordinateTeleportResultCode.Success);
    }

    [Fact]
    public async Task Integrated_host_dispatch_honors_server_mode_switches_without_bypassing_the_executor()
    {
        var executor = new HostRecordingExecutor();
        using var host = new AuthoritativeHostTeleportSession(
            "host-player",
            executor,
            new CoordinateTeleportServerOptions
            {
                SurfaceTeleportEnabled = false,
                WaypointTeleportEnabled = true,
            });

        var result = await host.RequestSurfaceAsync(
            10,
            20,
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Disabled, result);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Integrated_host_dispatch_executes_waypoint_through_the_authoritative_executor()
    {
        var executor = new HostRecordingExecutor();
        using var host = new AuthoritativeHostTeleportSession(
            "host-player",
            executor,
            new CoordinateTeleportServerOptions());
        var target = new Vector3(-12.5f, 42.25f, 88.5f);

        var result = await host.RequestWaypointAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Success, result);
        Assert.Equal(target, executor.WaypointTarget);
    }

    [Fact]
    public async Task Integrated_host_allows_safe_execution_to_outlast_the_network_deadline()
    {
        var executor = new HostRecordingExecutor
        {
            Delay = CoordinateTeleportServerSession.ExecutionDeadline
                + TimeSpan.FromMilliseconds(250),
        };
        using var host = new AuthoritativeHostTeleportSession(
            "host-player",
            executor,
            new CoordinateTeleportServerOptions());

        var result = await host.RequestSurfaceAsync(
            10,
            20,
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Success, result);
        Assert.Equal((10, 20), executor.SurfaceTarget);
    }

    [Theory]
    [InlineData(TeleportResult.Success, CoordinateTeleportResultCode.Success)]
    [InlineData(TeleportResult.NoSafePosition, CoordinateTeleportResultCode.NoSafePosition)]
    public async Task Integrated_host_reports_each_result_with_request_context(
        TeleportResult executorResult,
        CoordinateTeleportResultCode expectedResult)
    {
        var executor = new HostRecordingExecutor { Result = executorResult };
        var reported = new List<(CoordinateTeleportMessage Message, CoordinateTeleportResultCode Result)>();
        using var host = CreateObservedHost(
            executor,
            (message, result) => reported.Add((message, result)));
        var target = new Vector3(-12.5f, 42.25f, 88.5f);

        var result = await host.RequestWaypointAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedResult, result);
        var observation = Assert.Single(reported);
        Assert.Equal(CoordinateTeleportMessageKind.WaypointRequest, observation.Message.Kind);
        Assert.Equal(target, observation.Message.Target);
        Assert.Equal(expectedResult, observation.Result);
    }

    private static AuthoritativeHostTeleportSession CreateObservedHost(
        ICoordinateTeleportExecutor executor,
        Action<CoordinateTeleportMessage, CoordinateTeleportResultCode> reportResult) =>
        new(
            "host-player",
            executor,
            new CoordinateTeleportServerOptions(),
            reportResult);

    private static async Task WaitForCountAsync<T>(IReadOnlyCollection<T> values, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (values.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(values.Count >= count);
    }

    private sealed class HostRecordingExecutor : ICoordinateTeleportExecutor
    {
        internal int CallCount { get; private set; }

        internal TimeSpan Delay { get; init; }

        internal TeleportResult Result { get; init; } = TeleportResult.Success;

        internal Exception? Failure { get; init; }

        internal TeleportRequestDiagnosticContext? ObservedContext { get; private set; }

        internal (int X, int Z)? SurfaceTarget { get; private set; }

        internal Vector3? WaypointTarget { get; private set; }

        public async Task<TeleportResult> TeleportToSurfaceAsync(
            int x,
            int z,
            CancellationToken cancellationToken)
        {
            CallCount++;
            SurfaceTarget = (x, z);
            ObservedContext = TeleportDiagnosticContext.Current;
            await Task.Delay(Delay, cancellationToken);
            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }

        public async Task<TeleportResult> TeleportToWaypointAsync(
            Vector3 xyz,
            CancellationToken cancellationToken)
        {
            CallCount++;
            WaypointTarget = xyz;
            ObservedContext = TeleportDiagnosticContext.Current;
            await Task.Delay(Delay, cancellationToken);
            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }
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
    [Theory]
    [InlineData(CoordinateTeleportResultCode.Success)]
    [InlineData(CoordinateTeleportResultCode.Rejected)]
    public async Task Client_network_result_never_invokes_the_local_position_writer(
        CoordinateTeleportResultCode serverResult)
    {
        var clock = new AdapterProtocolClock();
        var sent = new List<CoordinateTeleportMessage>();
        var session = new CoordinateTeleportClientSession("server", sent.Add, clock, _ => { });
        var directPositionWrites = 0;
        Task<CoordinateTeleportResultCode>? networkOperation = null;
        var router = new TravelMapTeleportRouter(
            new TravelMapRuntimeContext(TravelMapWorkType.Client, IsMainPlayer: true, HasUi: true),
            (_, _) =>
            {
                directPositionWrites++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            authoritativeHostRequest: null,
            command => networkOperation = session.RequestSurfaceAsync(
                (int)command.Target.X,
                (int)command.Target.Z,
                CancellationToken.None));

        Assert.Equal(
            TravelMapTeleportDispatchResult.CommandQueued,
            await router.RequestSurfaceAsync(
                new Vector3(10f, 999f, 20f),
                TestContext.Current.CancellationToken));
        var capability = Assert.Single(sent);
        session.Receive(
            "server",
            CoordinateTeleportMessage.CapabilityResponse(capability.RequestId, true, true));
        await WaitForAdapterCountAsync(sent, 2);
        session.Receive(
            "server",
            CoordinateTeleportMessage.Result(sent[1].RequestId, serverResult));

        Assert.Equal(serverResult, await networkOperation!);
        Assert.Equal(0, directPositionWrites);
    }

    [Fact]
    public async Task Client_network_timeout_never_invokes_the_local_position_writer()
    {
        var clock = new AdapterProtocolClock();
        var directPositionWrites = 0;
        Task<CoordinateTeleportResultCode>? networkOperation = null;
        var session = new CoordinateTeleportClientSession("server", _ => { }, clock, _ => { });
        var router = new TravelMapTeleportRouter(
            new TravelMapRuntimeContext(TravelMapWorkType.Client, IsMainPlayer: true, HasUi: true),
            (_, _) =>
            {
                directPositionWrites++;
                return Task.FromResult(TravelMapTeleportDispatchResult.LocalRequested);
            },
            authoritativeHostRequest: null,
            command => networkOperation = session.RequestWaypointAsync(
                command.Target,
                CancellationToken.None));

        await router.RequestWaypointAsync(new Vector3(1f, 64f, 2f), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(CoordinateTeleportResultCode.TimedOut, await networkOperation!);
        Assert.Equal(0, directPositionWrites);
    }

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

    private static async Task WaitForAdapterCountAsync<T>(IReadOnlyCollection<T> values, int count)
    {
        while (values.Count < count)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    private sealed class AdapterProtocolClock : ICoordinateTeleportProtocolClock
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
            foreach (var pending in _delays.Where(item => item.Delay <= duration).ToArray())
            {
                pending.Completion.TrySetResult();
                _delays.Remove(pending);
            }
        }
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
