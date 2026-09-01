namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Notifies the tenant's administrator of a real human handoff (Fase 11,
/// Checkpoint 6). The only abstraction <see cref="ConversationMessageReceivedProcessor"/>
/// depends on for this — the concrete implementation (Infrastructure) is the
/// Exception #3 adapter that actually calls Communication's own
/// <c>SendHumanHandoffNotificationCommand</c>, mirroring exactly how
/// <see cref="IAgentResponseDeliveryService"/> keeps this cross-context call
/// out of the Application layer.
///
/// AIAgent never resolves, stores, or passes a destination/phone/channel —
/// Communication owns <c>AdministratorNotificationContact</c> entirely (CP6
/// mandate item 18/19/21) and resolves it internally from
/// <paramref name="tenantId"/> alone.
/// </summary>
public interface IAdministratorNotificationService
{
    Task<AdministratorNotificationResult> NotifyAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentHumanHandoffId, string reasonCode,
        CancellationToken cancellationToken);
}

/// <summary><see cref="FailureCode"/> is sanitized (mirrors <see cref="AgentResponseDeliveryResult.FailureCode"/>'s own discipline) — never a raw exception message.</summary>
public sealed record AdministratorNotificationResult(bool IsSuccess, string? FailureCode);
