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
        var binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token);

        Assert.NotNull(binding);
        Assert.True(binding.IsCurrent);

        var reconnectedClient = new object();
        current = current with { OwnerClient = reconnectedClient };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token) with
        {
            TokenId = Guid.NewGuid(),
        };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, new object(), playerGuid, token);
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token) with
        {
            IsConnected = false,
        };
        Assert.False(binding.IsCurrent);

        current = ValidState(client, playerData, player, playerGuid, token);
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
    public async Task Disconnect_during_bound_teleport_cancels_execution_and_suppresses_late_success()
    {
        var client = new object();
        var playerData = new object();
        var player = new object();
        var playerGuid = Guid.NewGuid();
        var token = Guid.NewGuid();
        var current = ValidState(client, playerData, player, playerGuid, token);
        using var removed = new CancellationTokenSource();
        var binding = TravelMapBoundPeer.TryCreate(() => current, player, removed.Token)!;
        var executor = new CancelAwareExecutor();
        using var session = new CoordinateTeleportServerSession(
            binding.Identity,
            executor,
            new CoordinateTeleportServerOptions());

        var operation = CoordinateTeleportBoundOperation.ExecuteAsync(
            binding,
            session,
            CoordinateTeleportMessage.WaypointRequest(12, new Vector3(1f, 64f, 1f)),
            CancellationToken.None);
        await executor.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        current = current with { IsConnected = false };
        removed.Cancel();

        var response = await operation.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CoordinateTeleportResultCode.Disconnected, response.ResultCode);
        Assert.True(executor.CancellationObserved);
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

    private sealed class CancelAwareExecutor : ICoordinateTeleportExecutor
    {
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool CancellationObserved { get; private set; }

        public Task<TeleportResult> TeleportToSurfaceAsync(
            int x,
            int z,
            CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        public Task<TeleportResult> TeleportToWaypointAsync(
            Vector3 xyz,
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
                CancellationObserved = true;
                throw;
            }
        }
    }
}
