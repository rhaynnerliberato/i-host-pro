namespace IHostPro.Contexts.Dashboard.Api.Contracts;

public sealed record DashboardHousekeepingOverviewResponse(
    int Pending,
    int InProgress,
    int Interrupted,
    int CompletedInPeriod,
    int CancelledInPeriod,
    int Delayed,
    int WaitingHelp,
    int WaitingMaterials);
