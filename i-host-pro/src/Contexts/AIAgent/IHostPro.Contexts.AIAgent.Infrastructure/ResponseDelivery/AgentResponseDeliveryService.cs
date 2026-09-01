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
///
/// Fase 11, Checkpoint 5 (mandate item 32): retries the call at most once
/// when the failure looks technical (anything other than the two known
/// permanent/data-state codes the handler returns) — <see cref="SendAgentResponseCommand"/>'s
/// own idempotency key is deterministic from Tenant/AgentInteraction/Channel
/// alone, so an identical retry naturally reuses the SAME key and can never
/// create a second <c>Message</c>. A permanent failure
/// (<c>ConversationNotFound</c>/<c>GuestContactOrPhoneNotAvailable</c>) is
/// never retried — repeating it would fail identically every time.
/// </summary>
public sealed class AgentResponseDeliveryService : IAgentResponseDeliveryService
{
    private static readonly HashSet<string> NonRetryableFailureCodes = new(StringComparer.Ordinal)
    {
        "ConversationNotFound",
        "GuestContactOrPhoneNotAvailable",
    };

    private readonly ICommunicationRequestDispatcher _communicationDispatcher;

    public AgentResponseDeliveryService(ICommunicationRequestDispatcher communicationDispatcher) =>
        _communicationDispatcher = communicationDispatcher;

    public async Task<AgentResponseDeliveryResult> SendAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentInteractionId, string content, CancellationToken cancellationToken)
    {
        var command = new SendAgentResponseCommand
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            ReservationId = reservationId,
            AgentInteractionId = agentInteractionId,
            Content = content,
        };

        var result = await _communicationDispatcher.Send(command, cancellationToken);

        if (result.IsFailure && !NonRetryableFailureCodes.Contains(result.Error.Code))
            result = await _communicationDispatcher.Send(command, cancellationToken);

        return result.IsFailure
            ? new AgentResponseDeliveryResult(false, null, result.Error.Code)
            : new AgentResponseDeliveryResult(true, result.Value.MessageId, null);
    }
}
