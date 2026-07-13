using System.Numerics;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Teleport;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class AdapterContractTests
{
    public static TheoryData<SurvivalcraftBlockMetadata, TeleportBlockKind> BlockKinds => new()
    {
        { new(IsAir: true), TeleportBlockKind.Air },
        { new(IsCollidable: true), TeleportBlockKind.SafeSolid },
        { new(IsFluid: true), TeleportBlockKind.Fluid },
        { new(IsLeaves: true), TeleportBlockKind.Leaves },
        { new(IsFalling: true), TeleportBlockKind.Falling },
        { new(IsDamaging: true), TeleportBlockKind.Damaging },
        { new(Hazard: SurvivalcraftBlockHazard.Lava), TeleportBlockKind.Lava },
        { new(Hazard: SurvivalcraftBlockHazard.Fire), TeleportBlockKind.Fire },
        { new(Hazard: SurvivalcraftBlockHazard.Cactus), TeleportBlockKind.Cactus },
        { new(Hazard: SurvivalcraftBlockHazard.Spikes), TeleportBlockKind.Spikes },
    };

    [Theory]
    [MemberData(nameof(BlockKinds))]
    public void Terrain_adapter_maps_game_metadata_to_explicit_safe_and_unsafe_kinds(
        SurvivalcraftBlockMetadata metadata,
        TeleportBlockKind expected)
    {
        var facade = new FakeTerrainFacade { Metadata = metadata };
        var adapter = new SurvivalcraftTerrainAccess(facade, facade.TeleportedPlayerBody);

        Assert.Equal(expected, adapter.GetBlockKind(1, 2, 3));
    }

    [Fact]
    public void Terrain_adapter_excludes_only_the_teleported_player_from_entity_collisions()
    {
        var facade = new FakeTerrainFacade { HasOtherBlockingEntity = true };
        var adapter = new SurvivalcraftTerrainAccess(facade, facade.TeleportedPlayerBody);

        Assert.True(adapter.HasBlockingCollisionExcludingPlayer(new Vector3(1.5f, 64f, 2.5f)));
        Assert.Same(facade.TeleportedPlayerBody, facade.LastExcludedBody);
    }

    [Fact]
    public async Task Chunk_adapter_polls_by_update_without_own_timeout_and_stops_at_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var facade = new FakeChunkFacade();
        var clock = new CancelOnFirstUpdateClock(cancellation);
        var adapter = new SurvivalcraftChunkLoader(facade, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.LoadAreaAsync(10, -20, 8, cancellation.Token));

        Assert.Equal([(10, -20, 8)], facade.Requests);
        Assert.Equal(1, facade.ReadinessChecks);
        Assert.Equal(1, clock.UpdateWaits);
        Assert.Empty(clock.Delays);
        Assert.Equal(1, facade.ReleaseCount);
    }

    [Fact]
    public async Task Chunk_adapter_holds_request_until_lease_disposal_then_allows_the_next_request()
    {
        var facade = new FakeChunkFacade { Ready = true };
        var clock = new FakeTeleportClock();
        using var adapter = new SurvivalcraftChunkLoader(facade, clock);

        var firstLease = await adapter.LoadAreaAsync(
            10,
            -20,
            8,
            TestContext.Current.CancellationToken);
        var secondLoad = adapter.LoadAreaAsync(
            30,
            -40,
            8,
            TestContext.Current.CancellationToken);

        Assert.False(secondLoad.IsCompleted);
        Assert.Equal([(10, -20, 8)], facade.Requests);
        Assert.Equal(0, facade.ReleaseCount);

        firstLease.Dispose();
        var secondLease = await secondLoad;
        Assert.Equal([(10, -20, 8), (30, -40, 8)], facade.Requests);
        Assert.Equal(1, facade.ReleaseCount);

        secondLease.Dispose();
        secondLease.Dispose();
        Assert.Equal(2, facade.ReleaseCount);
    }

    [Fact]
    public async Task Dispatcher_bound_chunk_facade_releases_a_worker_disposed_lease_on_the_update_thread()
    {
        using var dispatcher = new GameUpdateDispatcher();
        var updateThread = Environment.CurrentManagedThreadId;
        var facade = new FakeDispatcherBoundChunkFacade(dispatcher);
        using var adapter = new SurvivalcraftChunkLoader(facade, new FakeTeleportClock());
        var lease = await adapter.LoadAreaAsync(
            0,
            0,
            8,
            TestContext.Current.CancellationToken);

        var worker = Task.Run(lease.Dispose, TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, facade.ReleaseCount);

        dispatcher.Pump();
        await worker;

        Assert.Equal(1, facade.ReleaseCount);
        Assert.Equal(updateThread, facade.ReleaseThread);
    }

    [Fact]
    public void Player_adapter_captures_every_movement_field()
    {
        var expected = CreateMovement();
        var facade = new FakePlayerFacade { Movement = expected };
        var adapter = new SurvivalcraftPlayerMover(facade);

        Assert.Equal(expected, adapter.CaptureSnapshot());
    }

    [Fact]
    public void Player_adapter_move_preserves_pose_but_clears_both_velocities_and_fall_state()
    {
        var movement = CreateMovement() with { NativeState = new object() };
        var facade = new FakePlayerFacade();
        var adapter = new SurvivalcraftPlayerMover(facade);

        adapter.Move(movement);

        Assert.Equal(movement.Position, facade.Movement.Position);
        Assert.Equal(movement.Rotation, facade.Movement.Rotation);
        Assert.Equal(Vector3.Zero, facade.Movement.LinearVelocity);
        Assert.Equal(Vector3.Zero, facade.Movement.AngularVelocity);
        Assert.Equal(0f, facade.Movement.FallDistance);
        Assert.False(facade.Movement.IsFalling);
        Assert.False(facade.Movement.HasPendingFallDamage);
        Assert.Null(facade.Movement.NativeState);
    }

    [Fact]
    public void Player_adapter_restore_applies_the_passed_safe_snapshot_losslessly()
    {
        var nativeState = new object();
        var safeSnapshot = CreateMovement() with { NativeState = nativeState };
        var facade = new FakePlayerFacade();
        var adapter = new SurvivalcraftPlayerMover(facade);

        adapter.Restore(safeSnapshot);

        Assert.Equal(safeSnapshot, facade.Movement);
        Assert.Same(nativeState, facade.Movement.NativeState);
    }

    [Fact]
    public void Player_adapter_safe_restore_discards_native_state_and_clears_all_movement_transients()
    {
        var snapshot = CreateMovement() with { NativeState = new object() };
        var facade = new FakePlayerFacade();
        var adapter = new SurvivalcraftPlayerMover(facade);

        adapter.RestoreSafely(snapshot);

        Assert.Equal(snapshot.Position, facade.Movement.Position);
        Assert.Equal(snapshot.Rotation, facade.Movement.Rotation);
        Assert.Equal(Vector3.Zero, facade.Movement.LinearVelocity);
        Assert.Equal(Vector3.Zero, facade.Movement.AngularVelocity);
        Assert.Equal(0f, facade.Movement.FallDistance);
        Assert.False(facade.Movement.IsFalling);
        Assert.False(facade.Movement.HasPendingFallDamage);
        Assert.Null(facade.Movement.NativeState);
    }

    [Fact]
    public void Survivalcraft_state_codec_exactly_round_trips_collision_standing_and_fall_state()
    {
        var standingBody = new object();
        var engineState = new SurvivalcraftEngineMovementState(
            new Vector3(1f, 2f, 3f),
            Quaternion.CreateFromYawPitchRoll(0.5f, 0.25f, -0.1f),
            new Vector3(4f, 5f, 6f),
            new Vector3(7f, 8f, 9f),
            standingBody,
            42,
            new Vector3(10f, 11f, 12f),
            true,
            false);

        var snapshot = SurvivalcraftMovementStateCodec.Capture(engineState);
        var restored = SurvivalcraftMovementStateCodec.RestoreExact(snapshot);

        Assert.Equal(engineState, restored);
        Assert.Same(standingBody, restored.StandingBody);
    }

    [Fact]
    public void Survivalcraft_state_codec_safe_restore_preserves_pose_only_and_clears_native_transients()
    {
        var engineState = new SurvivalcraftEngineMovementState(
            new Vector3(1f, 2f, 3f),
            Quaternion.Identity,
            new Vector3(4f, 5f, 6f),
            new Vector3(7f, 8f, 9f),
            new object(),
            42,
            new Vector3(10f, 11f, 12f),
            true,
            false);
        var snapshot = SurvivalcraftMovementStateCodec.Capture(engineState);

        var safe = SurvivalcraftMovementStateCodec.RestoreSafely(snapshot);

        Assert.Equal(engineState.Position, safe.Position);
        Assert.Equal(engineState.Rotation, safe.Rotation);
        Assert.Equal(Vector3.Zero, safe.LinearVelocity);
        Assert.Equal(Vector3.Zero, safe.CollisionVelocityChange);
        Assert.Null(safe.StandingBody);
        Assert.Null(safe.StandingValue);
        Assert.Equal(Vector3.Zero, safe.StandingVelocity);
        Assert.False(safe.IsFalling);
        Assert.True(safe.WasStanding);
    }

    [Fact]
    public async Task Update_dispatcher_marshals_worker_engine_calls_to_the_update_thread()
    {
        using var dispatcher = new GameUpdateDispatcher();
        var updateThread = Environment.CurrentManagedThreadId;
        var actionThread = 0;
        var worker = Task.Run(() => dispatcher.Invoke(() => actionThread = Environment.CurrentManagedThreadId));
        Assert.True(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)));
        Assert.False(worker.IsCompleted);

        dispatcher.Pump();
        await worker;

        Assert.Equal(updateThread, actionThread);
    }

    [Fact]
    public async Task Update_dispatcher_next_update_wait_honors_cancellation()
    {
        using var dispatcher = new GameUpdateDispatcher();
        using var cancellation = new CancellationTokenSource();
        var nextUpdate = dispatcher.WaitForNextUpdateAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextUpdate);
    }

    [Fact]
    public async Task Next_update_continuation_resumes_inline_on_the_game_update_thread()
    {
        using var dispatcher = new GameUpdateDispatcher();
        var updateThread = Environment.CurrentManagedThreadId;
        var resumedThread = 0;

        async Task WaitAsync()
        {
            await dispatcher.WaitForNextUpdateAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            resumedThread = Environment.CurrentManagedThreadId;
        }

        var wait = WaitAsync();
        dispatcher.Pump();
        await wait;

        Assert.Equal(updateThread, resumedThread);
    }

    [Fact]
    public async Task Disposing_with_a_pending_worker_player_write_fails_it_without_running_the_delegate()
    {
        var dispatcher = new GameUpdateDispatcher();
        var facade = new FakePlayerFacade();
        var movement = CreateMovement();
        var writes = 0;
        var worker = Task.Run(() => dispatcher.Invoke(() =>
        {
            writes++;
            facade.WriteMovement(movement);
        }), TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)));

        dispatcher.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker);
        Assert.Equal(0, writes);
        Assert.Equal(default, facade.Movement);
    }

    [Fact]
    public async Task Disposing_during_a_claimed_pump_fails_the_write_before_dispose_returns()
    {
        using var disposeMarked = new ManualResetEventSlim();
        Task? disposal = null;
        GameUpdateDispatcher? dispatcher = null;
        dispatcher = new GameUpdateDispatcher(
            afterPumpClaimed: () =>
            {
                disposal = Task.Run(dispatcher!.Dispose, TestContext.Current.CancellationToken);
                Assert.True(disposeMarked.Wait(TimeSpan.FromSeconds(5)));
            },
            afterDisposeMarked: disposeMarked.Set);
        var writes = 0;
        var worker = Task.Run(
            () => dispatcher.Invoke(() => writes++),
            TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)));

        dispatcher.Pump();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker);
        await disposal!;
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Queued_action_can_invoke_reentrantly_on_the_update_thread()
    {
        using var dispatcher = new GameUpdateDispatcher();
        var nestedCalls = 0;
        var worker = Task.Run(
            () => dispatcher.Invoke(() => dispatcher.Invoke(() => nestedCalls++)),
            TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)));

        dispatcher.Pump();

        await worker;
        Assert.Equal(1, nestedCalls);
    }

    [Fact]
    public async Task Queued_action_can_dispose_reentrantly_without_waiting_for_its_own_pump()
    {
        var dispatcher = new GameUpdateDispatcher();
        var worker = Task.Run(
            () => dispatcher.Invoke(dispatcher.Dispose),
            TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)));

        dispatcher.Pump();

        await worker;
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Invoke(static () => { }));
    }

    [Theory]
    [InlineData(TravelMapWorkType.Local, true, true, true)]
    [InlineData(TravelMapWorkType.Server, false, true, false)]
    [InlineData(TravelMapWorkType.Client, true, false, false)]
    public void Work_type_policy_keeps_ui_and_direct_position_authority_separate(
        TravelMapWorkType workType,
        bool createsUi,
        bool createsTeleportService,
        bool allowsDirectPositionWrite)
    {
        Assert.Equal(createsUi, TravelMapRuntimePolicy.CreatesUi(workType));
        Assert.Equal(createsTeleportService, TravelMapRuntimePolicy.CreatesTeleportService(workType));
        Assert.Equal(allowsDirectPositionWrite, TravelMapRuntimePolicy.AllowsDirectPositionWrite(workType));
    }

    [Fact]
    public void Runtime_cleanup_releases_chunks_before_dispatcher_and_always_runs_final_cleanup()
    {
        var events = new List<string>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TravelMapRuntimePolicy.CleanupRuntime(
                () => events.Add("cancel"),
                () =>
                {
                    events.Add("chunks");
                    throw new InvalidOperationException("chunk cleanup failed");
                },
                () => events.Add("dispatcher"),
                () => events.Add("final")));

        Assert.Equal("chunk cleanup failed", exception.Message);
        Assert.Equal(["cancel", "chunks", "dispatcher", "final"], events);
    }

    private static PlayerMovementSnapshot CreateMovement() => new(
        new Vector3(10.5f, 65f, -3.5f),
        Quaternion.CreateFromYawPitchRoll(1f, 0.25f, -0.5f),
        new Vector3(1f, 2f, 3f),
        new Vector3(4f, 5f, 6f),
        12.5f,
        true,
        true);
}

internal sealed class FakeTerrainFacade : ISurvivalcraftTerrainFacade
{
    internal object TeleportedPlayerBody { get; } = new();

    internal SurvivalcraftBlockMetadata Metadata { get; set; }

    internal bool HasOtherBlockingEntity { get; set; }

    internal object? LastExcludedBody { get; private set; }

    public bool IsColumnInWorld(int x, int z) => true;

    public bool IsCellInWorld(int x, int y, int z) => true;

    public int GetSurfaceHeight(int x, int z) => 64;

    public SurvivalcraftBlockMetadata GetBlockMetadata(int x, int y, int z) => Metadata;

    public bool HasBlockingEntityCollision(Vector3 feetPosition, object excludedBody)
    {
        LastExcludedBody = excludedBody;
        return HasOtherBlockingEntity;
    }
}

internal sealed class FakeChunkFacade : ISurvivalcraftChunkFacade
{
    internal List<(int X, int Z, int Radius)> Requests { get; } = [];

    internal int ReadinessChecks { get; private set; }

    internal bool Ready { get; set; }

    internal int ReleaseCount { get; private set; }

    public void RequestArea(int centerX, int centerZ, int radius) => Requests.Add((centerX, centerZ, radius));

    public bool IsAreaReady(int centerX, int centerZ, int radius)
    {
        ReadinessChecks++;
        return Ready;
    }

    public void ReleaseArea() => ReleaseCount++;
}

internal sealed class FakeDispatcherBoundChunkFacade(GameUpdateDispatcher dispatcher)
    : DispatcherBoundChunkFacade(dispatcher)
{
    internal int ReleaseCount { get; private set; }

    internal int ReleaseThread { get; private set; }

    protected override void RequestAreaOnUpdateThread(int centerX, int centerZ, int radius)
    {
    }

    protected override bool IsAreaReadyOnUpdateThread(int centerX, int centerZ, int radius) => true;

    protected override void ReleaseAreaOnUpdateThread()
    {
        ReleaseCount++;
        ReleaseThread = Environment.CurrentManagedThreadId;
    }
}

internal sealed class CancelOnFirstUpdateClock(CancellationTokenSource cancellation) : ITeleportClock
{
    internal int UpdateWaits { get; private set; }

    internal List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Delays.Add(duration);
        return Task.Delay(duration, cancellationToken);
    }

    public Task WaitForNextUpdateAsync(CancellationToken cancellationToken)
    {
        UpdateWaits++;
        cancellation.Cancel();
        return Task.FromCanceled(cancellationToken);
    }
}

internal sealed class FakePlayerFacade : ISurvivalcraftPlayerFacade
{
    internal PlayerMovementSnapshot Movement { get; set; }

    public PlayerMovementSnapshot ReadMovement() => Movement;

    public void WriteMovement(PlayerMovementSnapshot movement) => Movement = movement;
}
