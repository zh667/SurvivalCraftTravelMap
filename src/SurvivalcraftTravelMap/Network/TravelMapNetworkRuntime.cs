using Game;
using Game.NetWork;
using System.Numerics;
using System.Runtime.CompilerServices;
using SurvivalcraftTravelMap.Mod;
using SurvivalcraftTravelMap.Teleport;

namespace SurvivalcraftTravelMap.Network;

internal static class TravelMapNetworkRuntime
{
    private static readonly ConditionalWeakTable<ProjectNet, LegacyServerContext> LegacyServers = new();

    internal static void HandleLegacy(
        LegacyGpsPackage package,
        ProjectNet projectNet,
        NetNode netNode,
        bool isServer)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(projectNet);
        ArgumentNullException.ThrowIfNull(netNode);
        if (isServer)
        {
            if (package.From is not null)
            {
                Observe(LegacyServers.GetValue(
                    projectNet,
                    static project => new LegacyServerContext(project))
                    .HandleAsync(package, netNode));
            }

            return;
        }

        var mainPlayer = projectNet.FindSubsystem<SubsystemPlayers>(false)?.MainPlayer;
        var component = mainPlayer?.Entity.FindComponent<TravelMapComponent>(false);
        if (component is not null
            && package.Message.Kind == LegacyGpsMessageKind.TeleportResponse)
        {
            component.HandleLegacyClientResponse(package.Message);
        }
    }

    internal static void UpdateLegacyServer(ProjectNet projectNet, NetNode netNode)
    {
        if (LegacyServers.TryGetValue(projectNet, out var context))
        {
            context.ExpireInvitations(netNode);
        }
    }

    internal static void HandleLegacyHost(
        LegacyGpsMessage message,
        ComponentPlayer sourcePlayer,
        ProjectNet projectNet,
        NetNode netNode)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sourcePlayer);
        ArgumentNullException.ThrowIfNull(projectNet);
        ArgumentNullException.ThrowIfNull(netNode);
        var source = sourcePlayer.PlayerData.Client;
        var package = new LegacyGpsPackage(message) { From = source };
        Observe(LegacyServers.GetValue(
            projectNet,
            static project => new LegacyServerContext(project))
            .HandleAsync(package, netNode));
    }

    internal static void HandleCoordinate(
        CoordinateTeleportPackage package,
        ProjectNet projectNet,
        NetNode netNode,
        bool isServer)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(projectNet);
        ArgumentNullException.ThrowIfNull(netNode);
        if (isServer)
        {
            var task = HandleCoordinateOnServerAsync(package, netNode);
            Observe(task);
            return;
        }

        var source = package.From;
        var mainPlayer = projectNet.FindSubsystem<SubsystemPlayers>(false)?.MainPlayer;
        var component = mainPlayer?.Entity.FindComponent<TravelMapComponent>(false);
        if (source is not null && component is not null)
        {
            component.ReceiveCoordinateServerMessage(source, package.Message);
        }
    }

    private static async Task HandleCoordinateOnServerAsync(
        CoordinateTeleportPackage package,
        NetNode netNode)
    {
        using var diagnosticScope = TeleportDiagnosticContext.Ensure(
            new TeleportRequestDiagnosticContext(
                "remote",
                package.Message.RequestId,
                package.Message.Kind.ToString()));
        var source = package.From;
        if (source is null)
        {
            return;
        }

        TravelMapBoundPeer? binding = null;
        await CoordinateTeleportResponseBoundary.ExecuteAsync(
            async () =>
            {
                var component = source.PlayerData?.ComponentPlayer?.Entity
                    .FindComponent<TravelMapComponent>(false);
                binding = component?.TryBindNetworkPeer(source);
                if (component is null || binding is null)
                {
                    return null;
                }

                return await component.HandleCoordinateServerAsync(
                    binding,
                    package.Message,
                    CancellationToken.None).ConfigureAwait(false);
            },
            () => binding is not null && binding.IsCurrent,
            response => netNode.QueuePackage(new CoordinateTeleportPackage(response) { To = source }),
            () => CoordinateTeleportMessage.Result(
                package.Message.RequestId,
                CoordinateTeleportResultCode.InternalError),
            TeleportDiagnosticReporter.Report).ConfigureAwait(false);
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class LegacyServerContext(ProjectNet project)
    {
        private readonly ProjectNet _project = project;
        private readonly InvitationManager _invitations = new();

        internal async Task HandleAsync(LegacyGpsPackage package, NetNode netNode)
        {
            var source = package.From;
            var sourcePlayer = FindPlayer(source.PlayerGuid);
            if (sourcePlayer is null || sourcePlayer.PlayerData.Client != source)
            {
                return;
            }

            switch (package.Message.Kind)
            {
                case LegacyGpsMessageKind.Request:
                    Send(netNode, source, LegacyGpsMessage.Response(
                        _project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers
                            .Where(player => player.PlayerGuid != sourcePlayer.PlayerGuid)
                            .Select(player => new LegacyGpsPlayerData(0, player.PlayerData.Name))
                            .ToArray()));
                    break;
                case LegacyGpsMessageKind.Teleport:
                    await HandleTeleportRequestAsync(package.Message, sourcePlayer, netNode)
                        .ConfigureAwait(false);
                    break;
                case LegacyGpsMessageKind.TeleportAllow:
                    await HandleInvitationResolutionAsync(
                        sourcePlayer,
                        package.Message.IsAllowed,
                        netNode).ConfigureAwait(false);
                    break;
                case LegacyGpsMessageKind.MultiServerTeleport:
                    SendResult(netNode, sourcePlayer, "此服务器不支持跨服务器玩家传送");
                    break;
            }
        }

        internal void ExpireInvitations(NetNode netNode)
        {
            foreach (var invitation in _invitations.RemoveExpired())
            {
                var inviter = FindPlayer(invitation.Inviter.Id);
                if (inviter is not null)
                {
                    SendResult(netNode, inviter, "玩家传送邀请等待超时");
                }
            }
        }

        private async Task HandleTeleportRequestAsync(
            LegacyGpsMessage message,
            ComponentPlayer inviter,
            NetNode netNode)
        {
            if (!Guid.TryParse(message.PlayerName, out var targetId))
            {
                SendResult(netNode, inviter, "目标玩家标识无效");
                return;
            }

            var invitee = FindPlayer(targetId);
            var request = _invitations.Request(
                ToInvitationPlayer(inviter),
                invitee is null
                    ? new LegacyInvitationPlayer(targetId, string.Empty, false)
                    : ToInvitationPlayer(invitee),
                inviter.PlayerData.ServerManager);
            switch (request.Status)
            {
                case InvitationRequestStatus.Self:
                    SendResult(netNode, inviter, "不能邀请自己传送");
                    break;
                case InvitationRequestStatus.TargetOffline:
                    SendResult(netNode, inviter, "目标玩家已离线");
                    break;
                case InvitationRequestStatus.AlreadyPending:
                    SendResult(netNode, inviter, "相关玩家已有待处理的传送邀请");
                    break;
                case InvitationRequestStatus.AdminImmediateTeleport:
                    await TeleportInviterAsync(inviter, invitee!, netNode).ConfigureAwait(false);
                    break;
                case InvitationRequestStatus.InvitationCreated:
                    SendResult(netNode, inviter, "传送邀请已发送");
                    Send(
                        netNode,
                        invitee!.PlayerData.Client,
                        LegacyGpsMessage.TeleportResponse(
                            1,
                            $"{inviter.PlayerData.Name} 邀请传送到你的位置，是否同意？"));
                    break;
            }
        }

        private async Task HandleInvitationResolutionAsync(
            ComponentPlayer invitee,
            bool accepted,
            NetNode netNode)
        {
            var resolution = _invitations.Resolve(invitee.PlayerGuid, accepted);
            if (resolution.Status is InvitationResolutionStatus.NotFound
                or InvitationResolutionStatus.Expired)
            {
                SendResult(netNode, invitee, "传送邀请已失效");
                return;
            }

            var inviter = FindPlayer(resolution.Invitation!.Inviter.Id);
            var currentInvitee = FindPlayer(resolution.Invitation.Invitee.Id);
            if (inviter is null || currentInvitee is null)
            {
                if (inviter is not null)
                {
                    SendResult(netNode, inviter, "对方已离线，传送邀请已取消");
                }

                return;
            }

            if (resolution.Status == InvitationResolutionStatus.Rejected)
            {
                SendResult(netNode, inviter, "对方拒绝了传送邀请");
                return;
            }

            await TeleportInviterAsync(inviter, currentInvitee, netNode).ConfigureAwait(false);
        }

        private async Task TeleportInviterAsync(
            ComponentPlayer inviter,
            ComponentPlayer invitee,
            NetNode netNode)
        {
            var component = inviter.Entity.FindComponent<TravelMapComponent>(false);
            if (component is null)
            {
                SendResult(netNode, inviter, "服务器地图传送组件不可用");
                return;
            }

            var position = invitee.ComponentBody.Position;
            var response = await LegacyInvitationTeleportExecution.ExecuteAsync(
                cancellationToken => component.HandleLegacyTeleportToPlayerAsync(
                    new Vector3(position.X, position.Y, position.Z),
                    cancellationToken),
                static result => result == TeleportResult.Success
                    ? "传送完成"
                    : result switch
                    {
                        TeleportResult.ChunkTimeout => "目标区块加载超时",
                        TeleportResult.NoSafePosition => "目标玩家附近没有安全落点",
                        TeleportResult.OutOfWorld => "目标位置超出世界范围",
                        TeleportResult.RolledBack => "落点复查失败，已回到原位置",
                        TeleportResult.Busy => "已有传送正在进行，请稍后再试",
                        _ => "传送失败，详细原因已写入日志",
                    },
                TeleportDiagnosticReporter.Report,
                CancellationToken.None).ConfigureAwait(false);
            SendResult(netNode, inviter, response);
        }

        private ComponentPlayer? FindPlayer(Guid playerId) =>
            _project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers
                .FirstOrDefault(player => player.PlayerGuid == playerId);

        private static LegacyInvitationPlayer ToInvitationPlayer(ComponentPlayer player) =>
            new(player.PlayerGuid, player.PlayerData.Name, player.PlayerData.Client.IsConnected);

        private static void SendResult(NetNode netNode, ComponentPlayer player, string message) =>
            Send(netNode, player.PlayerData.Client, LegacyGpsMessage.TeleportResponse(0, message));

        private static void Send(NetNode netNode, Client target, LegacyGpsMessage message)
        {
            if (target.IsConnected)
            {
                netNode.QueuePackage(new LegacyGpsPackage(message) { To = target });
            }
        }
    }
}

internal static class LegacyInvitationTeleportExecution
{
    internal const string FailureResponse = "传送失败，详细原因已写入日志";

    internal static async Task<string> ExecuteAsync(
        Func<CancellationToken, Task<TeleportResult>> executor,
        Func<TeleportResult, string> formatResult,
        Action<TeleportFailureDiagnostic> reportFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(formatResult);
        ArgumentNullException.ThrowIfNull(reportFailure);
        using var diagnosticScope = TeleportDiagnosticContext.Ensure(
            new TeleportRequestDiagnosticContext("invitation", null, "Teleport"));
        try
        {
            return formatResult(await executor(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!TeleportDiagnosticContext.HasReportedFailure)
            {
                try
                {
                    reportFailure(new TeleportFailureDiagnostic(
                        TeleportExecutionStage.ProtocolDispatch,
                        exception));
                }
                catch
                {
                    // A diagnostic sink must not fault the observed ID-41 task.
                }
                finally
                {
                    TeleportDiagnosticContext.MarkFailureReported();
                }
            }

            return FailureResponse;
        }
    }
}

internal static class CoordinateTeleportResponseBoundary
{
    internal static async Task ExecuteAsync(
        Func<Task<CoordinateTeleportMessage?>> createResponse,
        Func<bool> isBindingCurrent,
        Action<CoordinateTeleportMessage> sendResponse,
        Func<CoordinateTeleportMessage> createFailureResponse,
        Action<TeleportFailureDiagnostic> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(createResponse);
        ArgumentNullException.ThrowIfNull(isBindingCurrent);
        ArgumentNullException.ThrowIfNull(sendResponse);
        ArgumentNullException.ThrowIfNull(createFailureResponse);
        ArgumentNullException.ThrowIfNull(reportFailure);

        CoordinateTeleportMessage? response;
        try
        {
            response = await createResponse().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportProtocolDispatchIfNeeded(exception, reportFailure);
            response = createFailureResponse();
        }

        if (response is null)
        {
            return;
        }

        try
        {
            if (isBindingCurrent())
            {
                sendResponse(response);
            }
        }
        catch (Exception exception)
        {
            ReportProtocolDispatchIfNeeded(exception, reportFailure);
            throw;
        }
    }

    private static void ReportProtocolDispatchIfNeeded(
        Exception exception,
        Action<TeleportFailureDiagnostic> reportFailure)
    {
        if (TeleportDiagnosticContext.HasReportedFailure)
        {
            return;
        }

        try
        {
            reportFailure(new TeleportFailureDiagnostic(
                TeleportExecutionStage.ProtocolDispatch,
                exception));
        }
        catch
        {
            // A diagnostic sink must not replace the protocol failure.
        }
        finally
        {
            TeleportDiagnosticContext.MarkFailureReported();
        }
    }
}
