namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// Application-owned port, Infrastructure-implemented (mirrors
/// <c>IScheduleReader</c> exactly) — never exposes <c>IQueryable</c>, always
/// returns a fully materialized <see cref="DashboardOverviewResult"/>.
/// <paramref name="nowUtc"/> is passed in explicitly (never resolved
/// internally via <c>DateTimeOffset.UtcNow</c>) so the caller's own
/// <c>TimeProvider</c> is the single source of "now" — see
/// <see cref="GetDashboardOverviewQueryHandler"/>.
/// </summary>
public interface IDashboardOverviewReader
{
    Task<DashboardOverviewResult> GetOverviewAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
