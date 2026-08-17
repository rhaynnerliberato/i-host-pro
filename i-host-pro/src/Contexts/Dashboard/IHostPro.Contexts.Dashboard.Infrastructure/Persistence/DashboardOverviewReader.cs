using IHostPro.Contexts.Dashboard.Application.Overview;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence;

/// <inheritdoc cref="IDashboardOverviewReader"/>
/// <remarks>
/// Every indicator is computed via <c>COUNT</c>/<c>GROUP BY</c> translated by
/// EF Core — never a client-evaluated <c>ToListAsync()</c> followed by
/// in-memory counting (Checkpoint 2 mandate, §25). Several small, separate
/// queries per section is deliberate (mandate §25) — no single mega-query.
/// Relies on the ambient tenant-aware transaction
/// <c>TenantTransactionBehavior&lt;,,&gt;</c> already opens around the whole
/// query-handler call (RLS's <c>app.tenant_id</c> included) — mirrors
/// <c>ScheduleReader</c>'s own precedent exactly, no manual tenant filter
/// here (the DbContext's Global Query Filter already scopes every query).
/// <c>GroupBy</c> results are always re-sorted client-side, after
/// materialization — mandate §44: never rely on PostgreSQL's own
/// <c>GROUP BY</c> ordering.
/// </remarks>
public sealed class DashboardOverviewReader : IDashboardOverviewReader
{
    private static readonly string[] InProgressCleaningStatuses =
        ["InTransit", "Started", "InInspection", "WaitingHelp", "WaitingMaterials"];

    private readonly DashboardDbContext _dbContext;

    public DashboardOverviewReader(DashboardDbContext dbContext) => _dbContext = dbContext;

    public async Task<DashboardOverviewResult> GetOverviewAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var reservations = await GetReservationsOverviewAsync(from, to, nowUtc, cancellationToken);
        var housekeeping = await GetHousekeepingOverviewAsync(from, to, nowUtc, cancellationToken);
        var properties = await GetPropertiesOverviewAsync(cancellationToken);
        var occurrences = await GetOccurrencesOverviewAsync(from, to, cancellationToken);

        return new DashboardOverviewResult(
            new DashboardPeriodResult(from, to), reservations, housekeeping, properties, occurrences, nowUtc);
    }

    /// <summary>
    /// <see cref="DashboardReservationsOverviewResult.StatusCounts"/> is
    /// current-state — every reservation for the tenant, never filtered by
    /// <paramref name="from"/>/<paramref name="to"/> (mirrors
    /// Housekeeping/Properties' own current-state fields; only the fields
    /// explicitly named "...InPeriod" are period-filtered).
    /// </summary>
    private async Task<DashboardReservationsOverviewResult> GetReservationsOverviewAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var reservationsQuery = _dbContext.ReservationProjection.AsNoTracking();

        var checkIns = await reservationsQuery.CountAsync(
            r => r.CheckInAt != null && r.CheckInAt >= from && r.CheckInAt < to && r.Status != "cancelled",
            cancellationToken);

        var checkOuts = await reservationsQuery.CountAsync(
            r => r.CheckOutAt != null && r.CheckOutAt >= from && r.CheckOutAt < to && r.Status != "cancelled",
            cancellationToken);

        var future = await reservationsQuery.CountAsync(
            r => r.CheckInAt != null && r.CheckInAt >= nowUtc && r.Status != "cancelled",
            cancellationToken);

        var cancelledInPeriod = await reservationsQuery.CountAsync(
            r => r.CancelledAtUtc != null && r.CancelledAtUtc >= from && r.CancelledAtUtc < to,
            cancellationToken);

        var statusCounts = await reservationsQuery
            .GroupBy(r => r.Status)
            .Select(g => new DashboardStatusCountResult(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        return new DashboardReservationsOverviewResult(
            checkIns, checkOuts, future, cancelledInPeriod,
            statusCounts.OrderBy(s => s.Status, StringComparer.Ordinal).ToArray());
    }

    private async Task<DashboardHousekeepingOverviewResult> GetHousekeepingOverviewAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var cleaningsQuery = _dbContext.CleaningProjection.AsNoTracking();

        var pending = await cleaningsQuery.CountAsync(
            c => c.Status == "Pending" || c.Status == "Assigned", cancellationToken);

        var inProgress = await cleaningsQuery.CountAsync(
            c => InProgressCleaningStatuses.Contains(c.Status), cancellationToken);

        var interrupted = await cleaningsQuery.CountAsync(c => c.Status == "Interrupted", cancellationToken);

        var completedInPeriod = await cleaningsQuery.CountAsync(
            c => c.CompletedAtUtc != null && c.CompletedAtUtc >= from && c.CompletedAtUtc < to,
            cancellationToken);

        var cancelledInPeriod = await cleaningsQuery.CountAsync(
            c => c.CancelledAtUtc != null && c.CancelledAtUtc >= from && c.CancelledAtUtc < to,
            cancellationToken);

        // A ScheduledAtUtc == null Cleaning never counts as delayed (mandate
        // §19). Interrupted still counts if its ScheduledAtUtc already
        // passed — only Completed/Cancelled are excluded (deliberately no
        // silent exception for Interrupted).
        var delayed = await cleaningsQuery.CountAsync(
            c => c.ScheduledAtUtc != null && c.ScheduledAtUtc < nowUtc
                 && c.Status != "Completed" && c.Status != "Cancelled",
            cancellationToken);

        var waitingHelp = await cleaningsQuery.CountAsync(c => c.Status == "WaitingHelp", cancellationToken);
        var waitingMaterials = await cleaningsQuery.CountAsync(c => c.Status == "WaitingMaterials", cancellationToken);

        return new DashboardHousekeepingOverviewResult(
            pending, inProgress, interrupted, completedInPeriod, cancelledInPeriod, delayed, waitingHelp, waitingMaterials);
    }

    /// <summary>
    /// Current-state only — a Property still in Draft (never activated) is
    /// deliberately not counted in any of the three fields (mandate §22's
    /// own field list has no fourth Draft field).
    /// </summary>
    private async Task<DashboardPropertiesOverviewResult> GetPropertiesOverviewAsync(CancellationToken cancellationToken)
    {
        var propertiesQuery = _dbContext.PropertyProjection.AsNoTracking();

        var active = await propertiesQuery.CountAsync(p => p.Status == "active", cancellationToken);
        var inactive = await propertiesQuery.CountAsync(p => p.Status == "inactive", cancellationToken);
        var archived = await propertiesQuery.CountAsync(p => p.Status == "archived", cancellationToken);

        return new DashboardPropertiesOverviewResult(active, inactive, archived);
    }

    private async Task<DashboardOccurrencesOverviewResult> GetOccurrencesOverviewAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var occurrencesQuery = _dbContext.OccurrenceProjection.AsNoTracking()
            .Where(o => o.RegisteredAtUtc >= from && o.RegisteredAtUtc < to);

        var total = await occurrencesQuery.CountAsync(cancellationToken);

        var byType = await occurrencesQuery
            .GroupBy(o => o.Type)
            .Select(g => new DashboardOccurrenceTypeCountResult(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        return new DashboardOccurrencesOverviewResult(total, byType.OrderBy(t => t.Type, StringComparer.Ordinal).ToArray());
    }
}
