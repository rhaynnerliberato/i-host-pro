using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when an administrator explicitly requests that a guest's
/// access credential/instructions be delivered (Fase 10, Checkpoint 6.2 —
/// Guest Access Secure Delivery Corrective Implementation). Never
/// automatic — no publisher exists on <c>ReservationCreated</c>/
/// <c>GuestCheckedIn</c>, no scheduler infers timing (CP6.2 mandate item 9).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the GuestStayOperation's id/<c>"GuestStayOperation"</c>.
///
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — corrected: this
/// request genuinely has two real triggers (an administrator via the Api, or
/// the AI Agent acting on the guest's own explicit request), so
/// <see cref="IntegrationEvent.ActorType"/> is <c>"User"</c> or <c>"AI"/</c>
/// accordingly, never a hardcoded <c>"System"</c> — the previous
/// documentation here describing it as always <c>"System"</c> with the real
/// actor "captured only in the HTTP audit trail" understated the loss: no
/// such trail actually recorded which administrator triggered a given
/// resend. <see cref="IntegrationEvent.ActorId"/> is the administrator's id
/// or the AI Agent's own session id — never a fabricated human user.
///
/// Deliberately provider-neutral and minimal — no credential, no credential
/// reference, no instructions content, no GuestName, no GuestPhone (CP6.2
/// mandate item 12). Communication is the sole consumer — it resolves the
/// Property's guest access data itself, synchronously, via the new
/// exception #12 (<c>IPropertyGuestAccessReader</c>, ADR-028) at the moment
/// it is about to render and send.
/// </summary>
public sealed record GuestAccessDeliveryRequested : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }
}
