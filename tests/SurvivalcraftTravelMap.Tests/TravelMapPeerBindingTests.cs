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
        var binding = TravelMapBoundPeer.TryCreate(() => current, removed.Token);

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
            CancellationToken.None));
        Assert.Null(TravelMapBoundPeer.TryCreate(
            () => ValidState(client, playerData, player, playerGuid, token) with
            {
                PlayerGuid = Guid.NewGuid(),
            },
            CancellationToken.None));
        Assert.Null(TravelMapBoundPeer.TryCreate(
            () => ValidState(client, playerData, player, playerGuid, token) with
            {
                IsConnected = false,
            },
            CancellationToken.None));
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
        var binding = TravelMapBoundPeer.TryCreate(() => current, removed.Token)!;
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
