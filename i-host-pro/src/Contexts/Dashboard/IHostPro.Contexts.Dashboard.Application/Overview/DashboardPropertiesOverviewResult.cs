namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// Fase 7, Incremento 2, Checkpoint 2 (mandate §22). Current-state counts
/// only. A Property still in Draft (never activated) is deliberately not
/// counted here — the mandate's own field list is exactly these three, no
/// fourth Draft field.
/// </summary>
public sealed record DashboardPropertiesOverviewResult(int Active, int Inactive, int Archived);
