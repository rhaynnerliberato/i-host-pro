namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// A reservation as it appears in the paginated administrative listing (Fase
/// 3, Incremento 1 plan, item 9) — deliberately excludes
/// <c>GuestPhone</c>, mirroring
/// <c>PropertyManagement.Application.Properties.PropertySummaryResult</c>'s
/// own address omission.
/// </summary>
public sealed record ReservationSummaryResult(
    Guid Id,
    Guid PropertyId,
    string GuestName,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt,
    int GuestCount,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
