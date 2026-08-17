namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// The full Overview result (Fase 7, Incremento 2, Checkpoint 2) — carries
/// only operational counts, never PII (guest name/phone, housekeeper name,
/// occurrence description, address, revenue). <see cref="GeneratedAtUtc"/> is
/// response metadata only (the instant the query ran, via
/// <c>TimeProvider</c>) — never persisted, never a historical fact.
/// </summary>
public sealed record DashboardOverviewResult(
    DashboardPeriodResult Period,
    DashboardReservationsOverviewResult Reservations,
    DashboardHousekeepingOverviewResult Housekeeping,
    DashboardPropertiesOverviewResult Properties,
    DashboardOccurrencesOverviewResult Occurrences,
    DateTimeOffset GeneratedAtUtc);

/// <summary>The effectively processed interval — always equal to the request's own From/To, echoed back for clarity.</summary>
public sealed record DashboardPeriodResult(DateTimeOffset From, DateTimeOffset To);
