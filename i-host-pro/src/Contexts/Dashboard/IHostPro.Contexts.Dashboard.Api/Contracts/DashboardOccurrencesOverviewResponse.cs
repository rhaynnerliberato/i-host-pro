namespace IHostPro.Contexts.Dashboard.Api.Contracts;

public sealed record DashboardOccurrencesOverviewResponse(int TotalInPeriod, IReadOnlyList<DashboardOccurrenceTypeCountResponse> ByType);

public sealed record DashboardOccurrenceTypeCountResponse(string Type, int Count);
