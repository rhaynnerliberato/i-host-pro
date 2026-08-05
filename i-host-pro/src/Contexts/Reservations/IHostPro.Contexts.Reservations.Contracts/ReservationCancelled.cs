using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Published when a reservation transitions to <c>Cancelled</c> (Fase 3,
/// Incremento 1 plan, item 12) — terminal, never on a
/// rejected/already-cancelled attempt. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the reservation's
/// id/<c>"Reservation"</c>. <see cref="IntegrationEvent.ActorId"/> is the
/// Administrator/Operator who cancelled it. Carries no guest name/phone/
/// check-in/check-out/guest count — never business-sensitive content.
/// </summary>
public sealed record ReservationCancelled : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }
}
