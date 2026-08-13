namespace IHostPro.Contexts.Reservations.Api.Contracts;

/// <summary>
/// HTTP shape of <see cref="Application.Schedule.ScheduleItemResult"/> —
/// Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1). Deliberately
/// carries no guest name/phone or any other PII — this is a scheduling
/// shape, not a Reservation/Cleaning detail view.
/// </summary>
public sealed record ScheduleItemResponse(
    Guid Id,
    string Type,
    Guid PropertyId,
    DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc,
    string Status,
    Guid? HousekeeperUserId,
    Guid SourceReferenceId);
