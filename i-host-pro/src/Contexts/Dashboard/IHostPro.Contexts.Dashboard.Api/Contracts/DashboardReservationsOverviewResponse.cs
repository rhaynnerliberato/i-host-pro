namespace IHostPro.Contexts.Dashboard.Api.Contracts;

public sealed record DashboardReservationsOverviewResponse(
    int CheckInsInPeriod,
    int CheckOutsInPeriod,
    int FutureReservations,
    int CancelledInPeriod,
    IReadOnlyList<DashboardStatusCountResponse> StatusCounts);

public sealed record DashboardStatusCountResponse(string Status, int Count);
