using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Creates a new reservation, born <c>Confirmed</c> (Fase 3, Incremento 1
/// plan, item 6). <see cref="TenantId"/>/<see cref="ActorId"/> come
/// exclusively from the authenticated Administrator/Operator's access token
/// claims — a controller builds this from <c>ClaimsPrincipal</c>, never from
/// the request body. <see cref="CheckInAt"/>/<see cref="CheckOutAt"/> must
/// carry an explicit UTC offset on the way in (Fase 3, Incremento 1 plan,
/// item 6: "exigir offset explícito na entrada HTTP") — enforced by
/// <c>OptionalJsonConverter</c>'s underlying <c>DateTimeOffset</c> parsing at
/// the Api layer, never re-validated here.
/// </summary>
public sealed record CreateReservationCommand(
    Guid TenantId,
    Guid ActorId,
    Guid PropertyId,
    string GuestName,
    string? GuestPhone,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt,
    int GuestCount) : ICommand<ReservationResult>;
