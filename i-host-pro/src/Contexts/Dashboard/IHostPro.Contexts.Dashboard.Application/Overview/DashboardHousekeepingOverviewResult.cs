namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// Fase 7, Incremento 2, Checkpoint 2 (mandate §15-§21). Duration/SLA
/// averages are deliberately NOT part of this MVP indicator set.
/// </summary>
/// <param name="Pending">Current-state count: status in Pending or Assigned.</param>
/// <param name="InProgress">Current-state count: status in InTransit, Started, InInspection, WaitingHelp or WaitingMaterials.</param>
/// <param name="Interrupted">Current-state count: status = Interrupted.</param>
/// <param name="CompletedInPeriod">Cleanings with <c>CompletedAtUtc</c> in <c>[From, To)</c>.</param>
/// <param name="CancelledInPeriod">Cleanings with <c>CancelledAtUtc</c> in <c>[From, To)</c>.</param>
/// <param name="Delayed">Current-state count: <c>ScheduledAtUtc &lt; nowUtc</c> and status not in Completed/Cancelled (null <c>ScheduledAtUtc</c> never counts).</param>
/// <param name="WaitingHelp">Current-state count: status = WaitingHelp.</param>
/// <param name="WaitingMaterials">Current-state count: status = WaitingMaterials.</param>
public sealed record DashboardHousekeepingOverviewResult(
    int Pending,
    int InProgress,
    int Interrupted,
    int CompletedInPeriod,
    int CancelledInPeriod,
    int Delayed,
    int WaitingHelp,
    int WaitingMaterials);
