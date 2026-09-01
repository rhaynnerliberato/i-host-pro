using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Notifies the Tenant's administrator of a real human handoff (Fase 11,
/// Checkpoint 6 — Documento 17 Workflow 14's own "Notificar Administrador"
/// step). Mirrors <see cref="SendAgentResponseCommand"/>'s own shape and
/// discipline exactly, adapted for a different recipient: this Command
/// creates/sends a <see cref="Domain.Message"/> the same way, but the
/// destination is the Tenant's own <see cref="Domain.AdministratorNotificationContact"/>
/// (resolved internally, by <see cref="TenantId"/> alone), never the
/// guest's own phone.
///
/// <see cref="ConversationId"/>/<see cref="ReservationId"/> are carried only
/// as context (which guest conversation/reservation this handoff concerns —
/// <see cref="Domain.Message.Channel"/> is still resolved from
/// <see cref="Domain.Conversation.Channel"/>) — never as the message's own
/// destination. Deliberately never accepts a phone number, GuestName,
/// GuestPhone, credential, QR, raw prompt, or full conversation history (CP6
/// mandate item 27) — <see cref="ReasonCode"/> is the fixed, sanitized
/// allowlist value only.
///
/// Dispatched exclusively through <see cref="ICommunicationRequestDispatcher"/>
/// (Exception #3) from AI Agent's own Worker-hosted orchestrator — never
/// HTTP, never a service account/JWT.
/// </summary>
public sealed record SendHumanHandoffNotificationCommand : ICommand<SendHumanHandoffNotificationResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid AgentHumanHandoffId { get; init; }

    public required string ReasonCode { get; init; }
}
