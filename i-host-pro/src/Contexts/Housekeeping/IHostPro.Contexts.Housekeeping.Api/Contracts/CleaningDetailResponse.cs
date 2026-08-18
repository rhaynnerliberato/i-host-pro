namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

/// <summary>
/// A cleaning's full administrative detail — never carries <c>TenantId</c>
/// or <c>xmin</c> (Fase 6, Incremento 1). Mirrors
/// <c>ReservationDetailResponse</c>'s own shape.
/// </summary>
public sealed record CleaningDetailResponse(
    Guid Id,
    Guid PropertyId,
    Guid? ReservationId,
    Guid? AssignedHousekeeperUserId,
    string Status,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? InspectionStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc);
