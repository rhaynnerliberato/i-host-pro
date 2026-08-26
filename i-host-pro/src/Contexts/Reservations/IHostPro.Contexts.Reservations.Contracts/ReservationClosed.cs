using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Published when a reservation transitions to <c>Closed</c> (Fase 10,
/// Checkpoint 1 — Guest Operations Foundation) — terminal, the real
/// checkout outcome, never on an already-closed/rejected attempt. Mirrors
/// <see cref="ReservationCancelled"/>'s own shape exactly.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the reservation's id/<c>"Reservation"</c>. <see cref="IntegrationEvent.ActorType"/>
/// is always <c>"System"</c> — this transition has no human actor, it is
/// driven exclusively by the internal Guest Operations → Workflow →
/// Reservations checkout chain. Carries no guest name/phone/check-in/
/// check-out/guest count — never business-sensitive content.
/// </summary>
public sealed record ReservationClosed : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }
}
