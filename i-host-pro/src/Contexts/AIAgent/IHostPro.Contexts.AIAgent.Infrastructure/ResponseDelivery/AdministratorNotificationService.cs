using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.Communication.Application;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;

/// <summary>
/// The Exception #3 adapter behind <see cref="IAdministratorNotificationService"/>
/// (Fase 11, Checkpoint 6) — calls Communication's own
/// <see cref="SendHumanHandoffNotificationCommand"/> through
/// <see cref="ICommunicationRequestDispatcher"/>, never HTTP/JWT/a service
/// account. Lives in Infrastructure, never Application — mirrors exactly
/// where <see cref="AgentResponseDeliveryService"/>'s own cross-context call
/// lives. Never resolves, stores, or logs the administrator's own phone
/// number — Communication resolves it internally, entirely on its own side.
/// </summary>
public sealed class AdministratorNotificationService : IAdministratorNotificationService
{
    private readonly ICommunicationRequestDispatcher _communicationDispatcher;

    public AdministratorNotificationService(ICommunicationRequestDispatcher communicationDispatcher) =>
        _communicationDispatcher = communicationDispatcher;

    public async Task<AdministratorNotificationResult> NotifyAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentHumanHandoffId, string reasonCode,
        CancellationToken cancellationToken)
    {
        var result = await _communicationDispatcher.Send(
            new SendHumanHandoffNotificationCommand
            {
                TenantId = tenantId,
                ConversationId = conversationId,
                ReservationId = reservationId,
                AgentHumanHandoffId = agentHumanHandoffId,
                ReasonCode = reasonCode,
            },
            cancellationToken);

        return result.IsFailure
            ? new AdministratorNotificationResult(false, result.Error.Code)
            : new AdministratorNotificationResult(true, null);
    }
}
