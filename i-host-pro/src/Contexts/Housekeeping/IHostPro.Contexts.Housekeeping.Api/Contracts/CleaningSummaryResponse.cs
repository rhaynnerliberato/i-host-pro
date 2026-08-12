namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

/// <summary>A cleaning as it appears in the paginated administrative listing — mirrors <c>ReservationSummaryResponse</c>'s own shape.</summary>
public sealed record CleaningSummaryResponse(
    Guid Id,
    Guid PropertyId,
    Guid? ReservationId,
    Guid? AssignedHousekeeperUserId,
    string Status,
    DateTimeOffset CreatedAtUtc);
