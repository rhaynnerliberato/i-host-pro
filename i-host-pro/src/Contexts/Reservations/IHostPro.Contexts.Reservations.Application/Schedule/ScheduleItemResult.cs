namespace IHostPro.Contexts.Reservations.Application.Schedule;

/// <summary>
/// A single unified Agenda item (Fase 7, Incremento 1 — Agenda Foundation,
/// Checkpoint 1) — composes Reservation (this Bounded Context's own data)
/// and Cleaning (Housekeeping's own data, via <see cref="Projections.CleaningScheduleProjectionEntry"/>
/// — see that type's own doc comment) into one read shape. <see cref="Type"/>
/// is restricted, this increment, to the two sources that actually exist —
/// "Reservation"/"Cleaning" — never Maintenance/Reform/Visit/Block, which
/// are documented as future capacity (Documento 12 §7) but have no
/// implemented Bounded Context behind them yet.
/// </summary>
public sealed record ScheduleItemResult(
    Guid Id,
    string Type,
    Guid PropertyId,
    DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc,
    string Status,
    Guid? HousekeeperUserId,
    Guid SourceReferenceId);
