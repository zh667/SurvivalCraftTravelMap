namespace SurvivalcraftTravelMap.Network;

public sealed record LegacyInvitationPlayer(Guid Id, string Name, bool IsOnline);

public sealed record LegacyInvitation(
    Guid Id,
    LegacyInvitationPlayer Inviter,
    LegacyInvitationPlayer Invitee,
    DateTimeOffset ExpiresAtUtc);

public enum InvitationRequestStatus
{
    InvitationCreated,
    AdminImmediateTeleport,
    Self,
    TargetOffline,
    AlreadyPending,
}

public sealed record InvitationRequestResult(
    InvitationRequestStatus Status,
    LegacyInvitation? Invitation = null);

public enum InvitationResolutionStatus
{
    Accepted,
    Rejected,
    NotFound,
    Expired,
}

public sealed record InvitationResolutionResult(
    InvitationResolutionStatus Status,
    LegacyInvitation? Invitation = null);

public sealed class InvitationManager
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<LegacyInvitation> _pending = [];

    public InvitationManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<LegacyInvitation> Pending
    {
        get
        {
            lock (_sync)
            {
                RemoveExpired(_timeProvider.GetUtcNow());
                return _pending.ToArray();
            }
        }
    }

    public InvitationRequestResult Request(
        LegacyInvitationPlayer inviter,
        LegacyInvitationPlayer invitee,
        bool inviterIsAdministrator)
    {
        ArgumentNullException.ThrowIfNull(inviter);
        ArgumentNullException.ThrowIfNull(invitee);
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpired(now);
            if (inviter.Id == invitee.Id)
            {
                return new InvitationRequestResult(InvitationRequestStatus.Self);
            }

            if (!invitee.IsOnline)
            {
                return new InvitationRequestResult(InvitationRequestStatus.TargetOffline);
            }

            if (inviterIsAdministrator)
            {
                return new InvitationRequestResult(InvitationRequestStatus.AdminImmediateTeleport);
            }

            if (_pending.Any(invitation =>
                    invitation.Inviter.Id == inviter.Id
                    || invitation.Invitee.Id == inviter.Id
                    || invitation.Inviter.Id == invitee.Id
                    || invitation.Invitee.Id == invitee.Id))
            {
                return new InvitationRequestResult(InvitationRequestStatus.AlreadyPending);
            }

            var created = new LegacyInvitation(
                Guid.NewGuid(),
                inviter,
                invitee,
                now + InvitationLifetime);
            _pending.Add(created);
            return new InvitationRequestResult(InvitationRequestStatus.InvitationCreated, created);
        }
    }

    public InvitationResolutionResult Resolve(Guid inviteeId, bool accept)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var invitation = _pending.FirstOrDefault(item => item.Invitee.Id == inviteeId);
            if (invitation is null)
            {
                RemoveExpired(now);
                return new InvitationResolutionResult(InvitationResolutionStatus.NotFound);
            }

            _pending.Remove(invitation);
            if (now >= invitation.ExpiresAtUtc)
            {
                return new InvitationResolutionResult(InvitationResolutionStatus.Expired, invitation);
            }

            return new InvitationResolutionResult(
                accept ? InvitationResolutionStatus.Accepted : InvitationResolutionStatus.Rejected,
                invitation);
        }
    }

    public IReadOnlyList<LegacyInvitation> RemoveExpired()
    {
        lock (_sync)
        {
            return RemoveExpired(_timeProvider.GetUtcNow());
        }
    }

    private IReadOnlyList<LegacyInvitation> RemoveExpired(DateTimeOffset now)
    {
        var expired = _pending.Where(item => now >= item.ExpiresAtUtc).ToArray();
        foreach (var invitation in expired)
        {
            _pending.Remove(invitation);
        }

        return expired;
    }
}

public enum LegacyClientResponseAction
{
    Ignore,
    ShowMessage,
    ShowInvitation,
    RejectInvitation,
}

public static class LegacyInvitationClientPolicy
{
    public static LegacyClientResponseAction Decide(
        LegacyGpsMessage message,
        bool acceptTeleportInvitations)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Kind != LegacyGpsMessageKind.TeleportResponse)
        {
            return LegacyClientResponseAction.Ignore;
        }

        if (message.MessageType == 0)
        {
            return LegacyClientResponseAction.ShowMessage;
        }

        if (message.MessageType == 1)
        {
            return acceptTeleportInvitations
                ? LegacyClientResponseAction.ShowInvitation
                : LegacyClientResponseAction.RejectInvitation;
        }

        return LegacyClientResponseAction.Ignore;
    }
}
