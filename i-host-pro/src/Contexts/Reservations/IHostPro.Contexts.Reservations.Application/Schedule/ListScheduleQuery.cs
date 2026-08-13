using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Schedule;

/// <summary>
/// Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1) — read-only,
/// unified Agenda query. <see cref="From"/>/<see cref="To"/> are both
/// required (never an unbounded scan — see <see cref="ListScheduleQueryValidator"/>
/// for the explicit maximum window, an implementation decision registered
/// there, not a documented requirement). <see cref="EventType"/>, when
/// supplied, must be exactly "Reservation" or "Cleaning".
/// </summary>
public sealed record ListScheduleQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? PropertyId,
    Guid? HousekeeperUserId,
    string? EventType) : IQuery<IReadOnlyList<ScheduleItemResult>>;
