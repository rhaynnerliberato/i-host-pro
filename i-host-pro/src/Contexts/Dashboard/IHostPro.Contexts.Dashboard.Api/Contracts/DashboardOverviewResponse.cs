namespace IHostPro.Contexts.Dashboard.Api.Contracts;

/// <summary>
/// GET <c>/api/v1/dashboard/overview</c>'s response (Fase 7, Incremento 2,
/// Checkpoint 2). Carries only operational counts — no PII. <see cref="GeneratedAtUtc"/>
/// is response metadata only (the instant the query ran), never persisted.
/// </summary>
public sealed record DashboardOverviewResponse(
    DashboardPeriodResponse Period,
    DashboardReservationsOverviewResponse Reservations,
    DashboardHousekeepingOverviewResponse Housekeeping,
    DashboardPropertiesOverviewResponse Properties,
    DashboardOccurrencesOverviewResponse Occurrences,
    DateTimeOffset GeneratedAtUtc);

/// <summary>The effectively processed interval, half-open <c>[From, To)</c> — always equal to the request's own from/to.</summary>
public sealed record DashboardPeriodResponse(DateTimeOffset From, DateTimeOffset To);
