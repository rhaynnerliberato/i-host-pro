using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.Communication.Application;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;

/// <summary>
/// The Exception #3 adapter behind <see cref="IAgentResponseDeliveryService"/>
/// (Fase 11, Checkpoint 4) — calls Communication's own
/// <see cref="SendAgentResponseCommand"/> through
/// <see cref="ICommunicationRequestDispatcher"/>, never HTTP/JWT/a service
/// account. Lives in Infrastructure, never Application — mirrors exactly
/// where every write/read Tool's own cross-context call lives.
/// </summary>
public sealed class AgentResponseDeliveryService : IAgentResponseDeliveryService
{
    private readonly ICommunicationRequestDispatcher _communicationDispatcher;

    public AgentResponseDeliveryService(ICommunicationRequestDispatcher communicationDispatcher) =>
        _communicationDispatcher = communicationDispatcher;

    public async Task<AgentResponseDeliveryResult> SendAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentInteractionId, string content, CancellationToken cancellationToken)
    {
        var result = await _communicationDispatcher.Send(
            new SendAgentResponseCommand
            {
                TenantId = tenantId,
                ConversationId = conversationId,
                ReservationId = reservationId,
                AgentInteractionId = agentInteractionId,
                Content = content,
            },
            cancellationToken);

        return result.IsFailure
            ? new AgentResponseDeliveryResult(false, null, result.Error.Code)
            : new AgentResponseDeliveryResult(true, result.Value.MessageId, null);
    }
}
