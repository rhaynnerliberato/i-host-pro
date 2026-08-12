namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// A cleaning as it appears in the paginated administrative listing —
/// mirrors <c>ReservationSummaryResult</c>'s own shape, no field omitted
/// this increment (a Cleaning carries no personal/business-sensitive
/// content comparable to a Reservation's guest name/phone).
/// </summary>
public sealed record CleaningSummaryResult(
    Guid Id,
    Guid PropertyId,
    Guid? ReservationId,
    Guid? AssignedHousekeeperUserId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ScheduledAtUtc);
