using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Updates a reservation's property, guest name, guest phone, check-in,
/// check-out and/or guest count, idempotently (Fase 3, Incremento 1 plan,
/// item 8). <see cref="TenantId"/>/<see cref="ActorId"/> come exclusively
/// from the authenticated Administrator/Operator's access token claims.
/// Every field is <see cref="Optional{T}"/> to distinguish "omitted"
/// (<c>IsSet == false</c>, leave unchanged) from "explicitly supplied" — at
/// least one must be set, checked by the handler, mirroring
/// <c>UpdatePropertyCommand</c>'s <c>NoChangesProvided</c> precedent.
///
/// Only <see cref="GuestPhone"/> accepts an explicit <c>null</c> value (which
/// removes the phone) — every other field, when <c>IsSet</c>, must carry a
/// genuine, non-default value; an explicit JSON <c>null</c> on any of them
/// decodes to that type's default (<c>Guid.Empty</c>/<c>0</c>/
/// <c>DateTimeOffset.MinValue</c>/whitespace) and is rejected by the
/// validator's own presence check — no special "was this JSON null"
/// detection needed (Fase 3, Incremento 1 plan, item 8: "demais campos não
/// aceitam null").
/// </summary>
public sealed record UpdateReservationCommand(
    Guid TenantId,
    Guid ActorId,
    Guid ReservationId,
    Optional<Guid> PropertyId,
    Optional<string> GuestName,
    Optional<string?> GuestPhone,
    Optional<DateTimeOffset> CheckInAt,
    Optional<DateTimeOffset> CheckOutAt,
    Optional<int> GuestCount) : ICommand<ReservationResult>;
