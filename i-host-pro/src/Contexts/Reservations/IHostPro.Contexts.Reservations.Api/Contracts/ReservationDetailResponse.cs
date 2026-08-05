namespace IHostPro.Contexts.Reservations.Api.Contracts;

/// <summary>
/// Same fields as <see cref="ReservationSummaryResponse"/> plus
/// <see cref="GuestPhone"/> (Fase 3, Incremento 1 plan, item 9). Never
/// carries <c>TenantId</c>, <c>xmin</c>, audit data, or internal Property
/// detail beyond the opaque <see cref="PropertyId"/> (item 9: "não
/// retornar... detalhes internos do Imóvel").
/// </summary>
public sealed record ReservationDetailResponse(
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
