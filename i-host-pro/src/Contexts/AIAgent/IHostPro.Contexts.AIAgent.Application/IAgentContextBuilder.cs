namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 15. Builds the
/// minimal <see cref="ModelRequest"/> CP2 needs: sanitized Conversation
/// history only. Deliberately does NOT consult Reservations/GuestOperations/
/// Payments/Housekeeping/PropertyManagement/Policies via Tools — that is
/// Checkpoint 3's scope.
///
/// <paramref name="triggeringInboundMessageId"/> (Fase 11, Checkpoint 4):
/// the exact <c>Communication.Message.Id</c> of the inbound message this
/// call is processing. <see cref="Communication.Contracts.IConversationHistoryReader"/>
/// orders by <c>CreatedAtUtc</c> + a random-GUID tie-break — two messages
/// created microseconds apart (e.g. a proposal's own outbound response,
/// immediately followed by the guest's real-time confirmation reply) can
/// collide once truncated to Postgres <c>timestamptz</c> precision, and the
/// GUID tie-break gives no guarantee the more-recent one sorts last. The
/// implementation must guarantee the triggering message is always the FINAL
/// entry in <see cref="ModelRequest.Messages"/>, regardless of how the
/// reader itself ordered it — <see cref="IModelProvider"/>'s own marker/
/// intent detection always inspects only the last message.
///
/// <paramref name="reservationId"/> (Fase 11, Checkpoint 7): resolves the
/// Reservation's own Property, whose <c>AI_AGENT_BEHAVIOR</c> effective
/// policy (Configuration, GLOBAL → TENANT → PROPERTY) and configured IANA
/// time zone (nullable) compose <see cref="ModelRequest.SystemPrompt"/> —
/// never a hardcoded business prompt (Documento 16 §20), only a minimal safe
/// technical fallback plus whatever Configuration actually resolves.
/// </summary>
public interface IAgentContextBuilder
{
    Task<ModelRequest> BuildAsync(
        Guid tenantId, Guid conversationId, Guid triggeringInboundMessageId, Guid reservationId, CancellationToken cancellationToken);
}
