using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// Fase 7, Incremento 2 (Dashboard &amp; Reporting Foundation, Checkpoint 2)
/// — the single, read-only Overview query. <see cref="From"/>/<see cref="To"/>
/// are both required and interpreted as a half-open interval
/// <c>[From, To)</c> for every temporal indicator this query answers: an
/// instant exactly equal to <see cref="From"/> is included, an instant
/// exactly equal to <see cref="To"/> is excluded. Never an unbounded scan —
/// see <see cref="GetDashboardOverviewQueryValidator"/> for the explicit
/// maximum window (an implementation decision, mirrors
/// <c>ListScheduleQueryValidator</c>'s own 100-day precedent, not a
/// documented business requirement).
/// </summary>
public sealed record GetDashboardOverviewQuery(DateTimeOffset From, DateTimeOffset To) : IQuery<DashboardOverviewResult>;
