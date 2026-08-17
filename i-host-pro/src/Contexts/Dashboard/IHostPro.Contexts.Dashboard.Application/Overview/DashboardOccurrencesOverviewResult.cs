namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// Fase 7, Incremento 2, Checkpoint 2 (mandate §23-§24). No open/resolved
/// concept — <c>CleaningOccurrence</c> is append-only, with no lifecycle/
/// resolution state (Checkpoint 0 decision, §8).
/// </summary>
/// <param name="TotalInPeriod">Occurrences with <c>RegisteredAtUtc</c> in <c>[From, To)</c>.</param>
/// <param name="ByType">Distribution by type, over the same <c>[From, To)</c> filter.</param>
public sealed record DashboardOccurrencesOverviewResult(int TotalInPeriod, IReadOnlyList<DashboardOccurrenceTypeCountResult> ByType);

public sealed record DashboardOccurrenceTypeCountResult(string Type, int Count);
