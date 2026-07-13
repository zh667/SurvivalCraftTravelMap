using SurvivalcraftTravelMap.Network;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class LegacyGpsPackageTests
{
    public static TheoryData<LegacyGpsMessage, string> GoldenPayloads => new()
    {
        { LegacyGpsMessage.Request(), "00 00 00 00" },
        {
            LegacyGpsMessage.Response([new LegacyGpsPlayerData(7, "Alice")]),
            "01 00 00 00 29 5B 7B 22 53 65 72 76 65 72 4E 75 6D 62 65 72 22 3A 37 2C 22 50 6C 61 79 65 72 4E 61 6D 65 22 3A 22 41 6C 69 63 65 22 7D 5D"
        },
        {
            LegacyGpsMessage.Teleport("00112233-4455-6677-8899-aabbccddeeff"),
            "02 00 00 00 24 30 30 31 31 32 32 33 33 2D 34 34 35 35 2D 36 36 37 37 2D 38 38 39 39 2D 61 61 62 62 63 63 64 64 65 65 66 66"
        },
        {
            LegacyGpsMessage.MultiServerTeleport(42, "Alice"),
            "03 00 00 00 2A 00 00 00 05 41 6C 69 63 65"
        },
        {
            LegacyGpsMessage.TeleportResponse(2, "Denied"),
            "04 00 00 00 02 00 00 00 06 44 65 6E 69 65 64"
        },
        { LegacyGpsMessage.TeleportAllow(true), "05 00 00 00 01" },
    };

    [Theory]
    [MemberData(nameof(GoldenPayloads))]
    public void Codec_is_byte_compatible_with_original_34gps(
        LegacyGpsMessage message,
        string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex.Replace(" ", string.Empty));

        var encoded = LegacyGpsCodec.Serialize(message);
        var decoded = LegacyGpsCodec.Deserialize(encoded);

        Assert.Equal(expected, encoded);
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void Network_package_keeps_legacy_id_41()
    {
        Assert.Equal(41, LegacyGpsPackage.PackageId);
    }

    [Theory]
    [InlineData("", "truncated")]
    [InlineData("06000000", "kind")]
    [InlineData("02000000", "truncated")]
    [InlineData("0000000000", "trailing")]
    [InlineData("02000000818001", "too large")]
    public void Codec_rejects_malformed_truncated_oversized_and_trailing_payloads(
        string hex,
        string expectedMessage)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => LegacyGpsCodec.Deserialize(Convert.FromHexString(hex)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InvitationManagerTests
{
    [Fact]
    public void Requests_reject_self_and_offline_targets_and_admins_teleport_immediately()
    {
        var clock = new MutableTimeProvider();
        var manager = new InvitationManager(clock);
        var alice = new LegacyInvitationPlayer(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Alice", true);
        var bob = new LegacyInvitationPlayer(Guid.Parse("20000000-0000-0000-0000-000000000002"), "Bob", false);

        Assert.Equal(InvitationRequestStatus.Self, manager.Request(alice, alice, false).Status);
        Assert.Equal(InvitationRequestStatus.TargetOffline, manager.Request(alice, bob, false).Status);
        Assert.Equal(InvitationRequestStatus.AdminImmediateTeleport, manager.Request(alice, bob with { IsOnline = true }, true).Status);
        Assert.Empty(manager.Pending);
    }

    [Fact]
    public void Invitation_expires_after_30_seconds_and_cannot_be_resolved()
    {
        var clock = new MutableTimeProvider();
        var manager = new InvitationManager(clock);
        var alice = Player(1, "Alice");
        var bob = Player(2, "Bob");

        var request = manager.Request(alice, bob, false);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(InvitationRequestStatus.InvitationCreated, request.Status);
        Assert.Equal(InvitationResolutionStatus.Expired, manager.Resolve(bob.Id, true).Status);
        Assert.Empty(manager.Pending);
    }

    [Fact]
    public void One_player_cannot_participate_in_multiple_pending_invitations()
    {
        var manager = new InvitationManager(new MutableTimeProvider());
        var alice = Player(1, "Alice");
        var bob = Player(2, "Bob");
        var carol = Player(3, "Carol");

        Assert.Equal(InvitationRequestStatus.InvitationCreated, manager.Request(alice, bob, false).Status);
        Assert.Equal(InvitationRequestStatus.AlreadyPending, manager.Request(carol, alice, false).Status);
        Assert.Equal(InvitationRequestStatus.AlreadyPending, manager.Request(bob, carol, false).Status);
    }

    [Fact]
    public void Disabled_invitation_preference_auto_rejects_only_invitation_dialogs()
    {
        var invitation = LegacyGpsMessage.TeleportResponse(1, "Alice invites you");
        var ordinary = LegacyGpsMessage.TeleportResponse(0, "Target is offline");

        Assert.Equal(
            LegacyClientResponseAction.RejectInvitation,
            LegacyInvitationClientPolicy.Decide(invitation, acceptTeleportInvitations: false));
        Assert.Equal(
            LegacyClientResponseAction.ShowMessage,
            LegacyInvitationClientPolicy.Decide(ordinary, acceptTeleportInvitations: false));
        Assert.Equal(
            LegacyClientResponseAction.ShowInvitation,
            LegacyInvitationClientPolicy.Decide(invitation, acceptTeleportInvitations: true));
    }

    [Fact]
    public void Player_panel_has_exactly_four_players_per_page()
    {
        var players = Enumerable.Range(1, 9)
            .Select(i => new LegacyGpsPlayerData(i, $"Player {i}"))
            .ToArray();

        Assert.Equal(4, TeleportPanelModel.GetPage(players, 0).Count);
        Assert.Equal(4, TeleportPanelModel.GetPage(players, 1).Count);
        Assert.Single(TeleportPanelModel.GetPage(players, 2));
        Assert.Empty(TeleportPanelModel.GetPage(players, 3));
    }

    private static LegacyInvitationPlayer Player(int marker, string name) =>
        new(new Guid(marker, 0, 0, new byte[8]), name, true);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
