using SurvivalcraftTravelMap.Network;
using SurvivalcraftTravelMap.Teleport;
using System.Numerics;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapPeerBindingTests
{
    [Fact]
    public void Bound_peer_rejects_stale_client_token_player_swap_and_disconnect()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var current = ValidState(client, playerData, player, playerGuid, token);
        using var removed = new CancellationTokenSource();
        var binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token)!;

        Assert.True(binding.IsCurrent);

        var reconnectedClient = new object();
        current = current with { OwnerClient = reconnectedClient };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token);
        binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token)!;
        current = current with
        {
            TokenId = Guid.NewGuid(),
        };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token);
        binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token)!;
        current = current with { Player = new object() };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token);
        binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token)!;
        current = current with
        {
            IsConnected = false,
        };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token);
        binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token)!;
        removed.Cancel();
        Assert.False(binding.IsCurrent);
        Assert.True(binding.OperationToken.IsCancellationRequested);
    }

    [Fact]
    public void Bound_peer_requires_exact_owner_player_and_matching_guid_at_start()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();

        Assert.Null(TravelMapBoundPeer.TryCreate(
            () => ValidState(client, playerData, player, playerGuid, token) with
            {
                OwnerClient = new object(),
            },
            player,
            CancellationToken.None));
        Assert.Null(TravelMapBoundPeer.TryCreate(
            () => ValidState(client, playerData, player, playerGuid, token) with
            {
                PlayerGuid = Guid.NewGuid(),
            },
            player,
            CancellationToken.None));
        Assert.Null(TravelMapBoundPeer.TryCreate(
            () => ValidState(client, playerData, player, playerGuid, token) with
            {
                IsConnected = false,
            },
            player,
            CancellationToken.None));
    }

    [Fact]
    public void Lookup_to_bind_swap_with_same_guid_rejects_the_nonselected_player_before_execution()
    {
        var client = new object();
        var playerData = new object();
        var selectedPlayer = new object();
        var replacementPlayer = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var current = ValidState(client, playerData, selectedPlayer, playerGuid, token);
        var selectedDuringLookup = current.Player;
        current = ValidState(client, playerData, replacementPlayer, playerGuid, token);
        var executorCalls = 0;

        var binding = TravelMapBoundPeer.TryCreate(
            () => current,
            selectedDuringLookup!,
            CancellationToken.None);
        if (binding is not null)
        {
            executorCalls++;
        }

        Assert.Null(binding);
        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task Binding_invalidation_during_bound_teleport_cancels_and_rolls_back_without_sync()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var current = ValidState(client, playerData, player, playerGuid, token);
        var binding = TravelMapBoundPeer.TryCreate(
            () => current,
            player,
            CancellationToken.None)!;
        var terrain = new FakeTeleportTerrain();
        terrain.SetSafeFeet(1, 64, 1);
        var chunks = new FakeChunkLoader();
        var mover = new FakePlayerMover();
        var collisions = new FakeEntityCollisionQuery();
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mover.OnMove = () => moved.TrySetResult();
        var clock = new FakeTeleportClock
        {
            WaitForUpdate = cancellationToken =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
        };
        var positionSyncCount = 0;
        var service = new SafeTeleportService(
            terrain,
            chunks,
            mover,
            collisions,
            clock,
            () => positionSyncCount++);
        using var session = new CoordinateTeleportServerSession(
            binding.Identity,
            new SafeTeleportExecutor(service),
            new CoordinateTeleportServerOptions(),
            TimeSpan.FromSeconds(2));

        var operation = CoordinateTeleportBoundOperation.ExecuteAsync(
            binding,
            session,
            CoordinateTeleportMessage.WaypointRequest(12, new Vector3(1f, 64f, 1f)),
            CancellationToken.None);
        await moved.Task.WaitAsync(TestContext.Current.CancellationToken);
        current = current with { TokenId = Guid.NewGuid() };

        var response = await operation.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Disconnected, response.ResultCode);
        Assert.Single(mover.Movements);
        Assert.Single(mover.RestoredSnapshots);
        Assert.Equal(mover.Snapshot.Position, mover.RestoredSnapshots[0].Position);
        Assert.Equal(Vector3.Zero, mover.RestoredSnapshots[0].LinearVelocity);
        Assert.Equal(Vector3.Zero, mover.RestoredSnapshots[0].AngularVelocity);
        Assert.False(mover.RestoredSnapshots[0].HasPendingFallDamage);
        Assert.Equal(0, positionSyncCount);
    }

    [Fact]
    public async Task Observed_binding_invalidation_stays_disconnected_if_state_later_matches_again()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var original = ValidState(client, playerData, player, playerGuid, token);
        var current = original;
        var binding = TravelMapBoundPeer.TryCreate(
            () => current,
            player,
            CancellationToken.None)!;
        var executor = new CancellationGateExecutor();
        using var session = new CoordinateTeleportServerSession(
            binding.Identity,
            executor,
            new CoordinateTeleportServerOptions(),
            TimeSpan.FromSeconds(2));

        var operation = CoordinateTeleportBoundOperation.ExecuteAsync(
            binding,
            session,
            CoordinateTeleportMessage.WaypointRequest(13, new Vector3(1f, 64f, 1f)),
            CancellationToken.None);
        await executor.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        current = current with { IsConnected = false };
        await executor.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        current = original;
        executor.Release.TrySetResult();

        var response = await operation.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Disconnected, response.ResultCode);
    }

    [Fact]
    public async Task Brief_observed_binding_invalidation_cannot_revalidate_before_the_next_poll()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var original = ValidState(client, playerData, player, playerGuid, token);
        var current = original;
        TravelMapBoundPeer? binding = null;
        var executor = new CallbackExecutor(() =>
        {
            current = current with { Player = new object() };
            Assert.False(binding!.IsCurrent);
            current = original;
        });
        binding = TravelMapBoundPeer.TryCreate(
            () => current,
            player,
            CancellationToken.None)!;
        using var session = new CoordinateTeleportServerSession(
            binding.Identity,
            executor,
            new CoordinateTeleportServerOptions());

        var response = await CoordinateTeleportBoundOperation.ExecuteAsync(
            binding,
            session,
            CoordinateTeleportMessage.WaypointRequest(14, new Vector3(1f, 64f, 1f)),
            CancellationToken.None);

        Assert.Equal(CoordinateTeleportResultCode.Disconnected, response.ResultCode);
    }

    [Fact]
    public async Task Binding_invalidated_at_commit_gate_rolls_back_before_authoritative_sync()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var current = ValidState(client, playerData, player, playerGuid, token);
        var binding = TravelMapBoundPeer.TryCreate(
            () => current,
            player,
            CancellationToken.None)!;
        var terrain = new FakeTeleportTerrain();
        terrain.SetSafeFeet(1, 64, 1);
        var mover = new FakePlayerMover();
        var positionSyncCount = 0;
        var committer = new InvalidatingPositionCommitter(
            () => current = current with { OwnerClient = new object() },
            () => positionSyncCount++);
        var service = new SafeTeleportService(
            terrain,
            new FakeChunkLoader(),
            mover,
            new FakeEntityCollisionQuery(),
            committer,
            new FakeTeleportClock(),
            static _ => { });
        using var session = new CoordinateTeleportServerSession(
            binding.Identity,
            new SafeTeleportExecutor(service),
            new CoordinateTeleportServerOptions());

        var response = await CoordinateTeleportBoundOperation.ExecuteAsync(
            binding,
            session,
            CoordinateTeleportMessage.WaypointRequest(15, new Vector3(1f, 64f, 1f)),
            CancellationToken.None);

        Assert.Equal(CoordinateTeleportResultCode.Disconnected, response.ResultCode);
        Assert.Single(mover.Movements);
        Assert.Single(mover.RestoredSnapshots);
        Assert.Equal(mover.Snapshot.Position, mover.RestoredSnapshots[0].Position);
        Assert.Equal(0, positionSyncCount);
    }

    private static TravelMapPeerState ValidState(
        object client,
        object playerData,
        object player,
        Guid playerGuid,
        Guid token) =>
        new(
            client,
            client,
            playerData,
            player,
            playerGuid,
            playerGuid,
            token,
            7,
            true);

    private sealed class CancellationGateExecutor :
        ICoordinateTeleportExecutor,
        IGuardedCoordinateTeleportExecutor
    {
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TeleportResult> TeleportToSurfaceAsync(
            int x,
            int z,
            CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        public Task<TeleportResult> TeleportToWaypointAsync(
            Vector3 xyz,
            CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        Task<TeleportResult> IGuardedCoordinateTeleportExecutor.TeleportToSurfaceAsync(
            int x,
            int z,
            Func<bool> commitGuard,
            CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        Task<TeleportResult> IGuardedCoordinateTeleportExecutor.TeleportToWaypointAsync(
            Vector3 xyz,
            Func<bool> commitGuard,
            CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        private async Task<TeleportResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return TeleportResult.Success;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
                throw;
            }
        }
    }

    private sealed class CallbackExecutor(Action callback) :
        ICoordinateTeleportExecutor,
        IGuardedCoordinateTeleportExecutor
    {
        private readonly Action _callback = callback;

        public Task<TeleportResult> TeleportToSurfaceAsync(
            int x,
            int z,
            CancellationToken cancellationToken) => ExecuteAsync();

        public Task<TeleportResult> TeleportToWaypointAsync(
            Vector3 xyz,
            CancellationToken cancellationToken) => ExecuteAsync();

        Task<TeleportResult> IGuardedCoordinateTeleportExecutor.TeleportToSurfaceAsync(
            int x,
            int z,
            Func<bool> commitGuard,
            CancellationToken cancellationToken) => ExecuteGuardedAsync(commitGuard);

        Task<TeleportResult> IGuardedCoordinateTeleportExecutor.TeleportToWaypointAsync(
            Vector3 xyz,
            Func<bool> commitGuard,
            CancellationToken cancellationToken) => ExecuteGuardedAsync(commitGuard);

        private Task<TeleportResult> ExecuteAsync()
        {
            _callback();
            return Task.FromResult(TeleportResult.Success);
        }

        private Task<TeleportResult> ExecuteGuardedAsync(Func<bool> commitGuard)
        {
            _callback();
            if (!commitGuard())
            {
                throw new OperationCanceledException("The bound peer changed before commit.");
            }

            return Task.FromResult(TeleportResult.Success);
        }
    }

    private sealed class InvalidatingPositionCommitter(
        Action beforeGuard,
        Action synchronizePosition) : ITeleportPositionCommitter
    {
        private readonly Action _beforeGuard = beforeGuard;
        private readonly Action _synchronizePosition = synchronizePosition;

        public void Commit(Func<bool> commitGuard)
        {
            _beforeGuard();
            if (!commitGuard())
            {
                throw new OperationCanceledException("The bound peer changed before position commit.");
            }

            _synchronizePosition();
        }
    }

}
