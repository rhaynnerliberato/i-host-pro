namespace IHostPro.Contexts.Reservations.Api.Contracts;

public sealed record ReservationSummaryResponse(
    Guid Id,
    Guid PropertyId,
    string GuestName,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt,
    int GuestCount,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
