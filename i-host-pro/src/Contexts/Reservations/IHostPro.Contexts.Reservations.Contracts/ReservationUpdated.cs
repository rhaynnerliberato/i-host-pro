using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Published when an existing reservation's property, guest name, guest
/// phone, check-in, check-out and/or guest count changes (Fase 3, Incremento
/// 1 plan, item 12) — never for a no-op update. <see cref="ChangedFields"/>
/// names which fields changed (<c>"property_id"</c>/<c>"guest_name"</c>/
/// <c>"guest_phone"</c>/<c>"check_in_at"</c>/<c>"check_out_at"</c>/
/// <c>"guest_count"</c>, in that order) — <see cref="CheckInAt"/>/
/// <see cref="CheckOutAt"/> below always carry the reservation's CURRENT
/// values after this update, regardless of which specific field(s) actually
/// changed (so a consumer maintaining a mirrored projection never needs to
/// diff — it always has the latest state).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the updated reservation's id/<c>"Reservation"</c>.
/// <see cref="IntegrationEvent.ActorId"/> is the Administrator/Operator who
/// made the change.
///
/// <see cref="CheckInAt"/>/<see cref="CheckOutAt"/> (Fase 7, Incremento 2 —
/// Dashboard & Reporting Foundation, Checkpoint 0/1 decision): same
/// additive-compatibility rationale as <see cref="ReservationCreated"/> —
/// nullable so an old, already-persisted envelope from before this field
/// existed deserializes as <c>null</c> ("unknown"), never
/// <see cref="DateTimeOffset.MinValue"/>; the producer always populates both
/// with the real post-update domain values.
/// </summary>
public sealed record ReservationUpdated : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required IReadOnlyCollection<string> ChangedFields { get; init; }

    public DateTimeOffset? CheckInAt { get; init; }

    public DateTimeOffset? CheckOutAt { get; init; }
}
