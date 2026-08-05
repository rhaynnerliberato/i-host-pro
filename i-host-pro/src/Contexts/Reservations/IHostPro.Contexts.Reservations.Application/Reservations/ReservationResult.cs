namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// A reservation's full administrative detail — the shape shared by
/// <c>CreateReservationCommand</c>'s response, <c>UpdateReservationCommand</c>'s
/// response, <c>CancelReservationCommand</c>'s response, and
/// <c>GetReservationDetailQuery</c>'s result. Carries <see cref="GuestPhone"/>
/// (the summary shape omits it) — never any internal Property detail beyond
/// its opaque id (Fase 3, Incremento 1 plan, item 9: "não retornar... detalhes
/// internos do Imóvel").
/// </summary>
public sealed record ReservationResult(
    Guid Id,
    Guid PropertyId,
    string GuestName,
    string? GuestPhone,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt,
    int GuestCount,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
