namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// A cleaning's full administrative detail — the shape shared by every
/// write command's response and <c>GetCleaningDetailQuery</c>'s result,
/// mirroring <c>ReservationResult</c>.
/// </summary>
public sealed record CleaningResult(
    Guid Id,
    Guid PropertyId,
    Guid? ReservationId,
    Guid? AssignedHousekeeperUserId,
    string Status,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? InspectionStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc);
