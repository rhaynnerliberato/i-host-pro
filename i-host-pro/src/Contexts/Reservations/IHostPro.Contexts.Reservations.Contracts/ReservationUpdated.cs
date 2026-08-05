using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Published when an existing reservation's property, guest name, guest
/// phone, check-in, check-out and/or guest count changes (Fase 3, Incremento
/// 1 plan, item 12) — never for a no-op update. <see cref="ChangedFields"/>
/// names which fields changed (<c>"property_id"</c>/<c>"guest_name"</c>/
/// <c>"guest_phone"</c>/<c>"check_in_at"</c>/<c>"check_out_at"</c>/
/// <c>"guest_count"</c>, in that order), never their new values.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the updated reservation's id/<c>"Reservation"</c>.
/// <see cref="IntegrationEvent.ActorId"/> is the Administrator/Operator who
/// made the change.
/// </summary>
public sealed record ReservationUpdated : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required IReadOnlyCollection<string> ChangedFields { get; init; }
}
