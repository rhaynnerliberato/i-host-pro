using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Published when a new reservation is created (Fase 3, Incremento 1 plan,
/// item 12) — always <c>Status = "confirmed"</c>, since every reservation is
/// born <c>Confirmed</c> this increment. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the new reservation's
/// id/<c>"Reservation"</c>. <see cref="IntegrationEvent.ActorId"/> is the
/// Administrator/Operator who created it. Carries no guest name/phone/
/// check-in/check-out/guest count — never personal or business-sensitive
/// content (Fase 3, Incremento 1 plan, item 12).
/// </summary>
public sealed record ReservationCreated : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required string Status { get; init; }
}
