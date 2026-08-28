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
/// <see cref="IntegrationEvent.ActorType"/> is always <c>"System"</c> — the
/// human actor (the requesting Administrator) is captured only in this
/// process's own HTTP audit trail, never republished here.
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
