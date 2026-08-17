namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// Fase 7, Incremento 2, Checkpoint 2 (mandate §9-§14). Occupancy,
/// revenue, average stay, guest count, nights and availability are
/// deliberately NOT part of this MVP indicator set.
/// </summary>
/// <param name="CheckInsInPeriod">Reservations with <c>CheckInAt</c> in <c>[From, To)</c> and current status not cancelled.</param>
/// <param name="CheckOutsInPeriod">Reservations with <c>CheckOutAt</c> in <c>[From, To)</c> and current status not cancelled.</param>
/// <param name="FutureReservations">Current-state count: <c>CheckInAt &gt;= nowUtc</c> and status not cancelled.</param>
/// <param name="CancelledInPeriod">Reservations cancelled with <c>CancelledAtUtc</c> in <c>[From, To)</c>.</param>
/// <param name="StatusCounts">Current-state count by status, over every reservation for the tenant (not period-filtered).</param>
public sealed record DashboardReservationsOverviewResult(
    int CheckInsInPeriod,
    int CheckOutsInPeriod,
    int FutureReservations,
    int CancelledInPeriod,
    IReadOnlyList<DashboardStatusCountResult> StatusCounts);

public sealed record DashboardStatusCountResult(string Status, int Count);
