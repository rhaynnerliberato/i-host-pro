using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Published when a new reservation is created (Fase 3, Incremento 1 plan,
/// item 12) — always <c>Status = "confirmed"</c>, since every reservation is
/// born <c>Confirmed</c> this increment. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the new reservation's
/// id/<c>"Reservation"</c>. <see cref="IntegrationEvent.ActorId"/> is the
/// Administrator/Operator who created it. Carries no guest name/phone/guest
/// count — never personal content (Fase 3, Incremento 1 plan, item 12).
///
/// <see cref="CheckInAt"/>/<see cref="CheckOutAt"/> (Fase 7, Incremento 2 —
/// Dashboard & Reporting Foundation, Checkpoint 0/1 decision) are the real
/// domain values from <c>Reservation.CheckInAt</c>/<c>Reservation.CheckOutAt</c>
/// — the operational temporal dimension Dashboard needs, never PII, never
/// recalculated. Nullable purely for additive-contract compatibility (a
/// consumer built against this new shape reading an old, already-persisted
/// envelope from before this field existed must receive <c>null</c>, never a
/// deserialization failure or a fabricated default) — the producer always
/// populates both, since every real <c>Reservation</c> always has both dates.
/// A consumer must treat <c>null</c> as "unknown", never as
/// <see cref="DateTimeOffset.MinValue"/>.
///
/// <see cref="Source"/> (Fase 9, Checkpoint 3.2 — "Airbnb Deterministic
/// Foundation") is the stable lowercase code for the reservation's
/// <c>ReservationSource</c> — <c>"manual"</c> or <c>"airbnb"</c>, mirroring
/// <see cref="Status"/>'s own string-code convention rather than exposing the
/// Domain enum directly. Every producer of this event must populate it —
/// there is no legacy envelope from before this field existed (unlike
/// <see cref="CheckInAt"/>/<see cref="CheckOutAt"/> above), so it is
/// <c>required</c>, not nullable.
/// </summary>
public sealed record ReservationCreated : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? CheckInAt { get; init; }

    public DateTimeOffset? CheckOutAt { get; init; }

    public required string Source { get; init; }
}
