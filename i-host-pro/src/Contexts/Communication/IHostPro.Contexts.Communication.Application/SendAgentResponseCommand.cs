using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Delivers the AI Agent's own response as a real outbound
/// <see cref="Domain.Message"/> (Fase 11, Checkpoint 4 — Write Tools &amp;
/// Response Delivery; Documento 13 §30's own <c>IA → Application Service →
/// Communication Module → WhatsApp Adapter → WhatsApp</c> chain). This is
/// Communication's FIRST synchronous Command — every other outbound send in
/// this Bounded Context reacts to an Integration Event through a dedicated
/// processor; a guest-facing AI response has no such event to react to,
/// since the content is already decided, in-process, by the same call that
/// is about to send it (CP4 mandate item 22, an intentional, approved change
/// of pattern — never a precedent for a generic "send anything" Command).
///
/// Dispatched exclusively through <see cref="ICommunicationRequestDispatcher"/>
/// (Exception #3, mirroring every other per-context request dispatcher
/// already in this codebase) from AI Agent's own Worker-hosted orchestrator
/// — never HTTP, never a service account/JWT.
///
/// Deliberately never accepts a phone number, provider id, channel
/// override, or any secret (CP4 mandate item 23) — <see cref="ConversationId"/>/
/// <see cref="ReservationId"/> let the handler resolve the recipient/channel
/// itself, exactly like every other Communication processor already does
/// (<c>IReservationGuestContactReader</c>, ADR-019/Exception #5;
/// <c>Conversation.Channel</c>).
/// </summary>
public sealed record SendAgentResponseCommand : ICommand<SendAgentResponseResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid AgentInteractionId { get; init; }

    public required string Content { get; init; }
}
