using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Persistence;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class TravelMapStorageIdentityTests
{
    [Fact]
    public void Local_storage_uses_world_directory_and_player_guid()
    {
        var player = Guid.Parse("7b570f99-8a40-40bd-9685-522f5b3ec3fc");
        var input = new TravelMapStorageIdentityInput(
            TravelMapStorageScope.LocalWorld,
            "C:/app",
            "C:/Worlds/Survival One",
            null,
            null,
            null,
            player);

        var resolved = TravelMapStorageIdentity.TryResolve(input, out var location, out _);

        Assert.True(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                "C:/app",
                "maps",
                WorldKey.ForLocal("C:/Worlds/Survival One"),
                player.ToString("N"))),
            location!.Directory);
    }

    [Fact]
    public void Client_storage_uses_server_endpoint_world_id_and_local_player_guid()
    {
        var player = Guid.Parse("6c413954-9438-4747-ad91-e561f76949c1");
        var input = new TravelMapStorageIdentityInput(
            TravelMapStorageScope.RemoteServer,
            "C:/app",
            null,
            "play.example.test",
            25565,
            "server-world-7",
            player);

        Assert.True(TravelMapStorageIdentity.TryResolve(input, out var location, out _));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                "C:/app",
                "maps",
                WorldKey.ForServer("play.example.test", 25565, "server-world-7"),
                player.ToString("N"))),
            location!.Directory);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Missing_client_endpoint_or_identity_disables_persistence(
        bool missingEndpoint,
        bool missingPlayer)
    {
        var input = new TravelMapStorageIdentityInput(
            TravelMapStorageScope.RemoteServer,
            "C:/app",
            null,
            missingEndpoint ? null : "play.example.test",
            missingEndpoint ? null : 25565,
            "server-world-7",
            missingPlayer ? Guid.Empty : Guid.NewGuid());

        Assert.False(TravelMapStorageIdentity.TryResolve(input, out var location, out var reason));
        Assert.Null(location);
        Assert.NotEmpty(reason);
    }
}
