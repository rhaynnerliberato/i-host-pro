namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record PagedPropertyResponse(int Page, int PageSize, int TotalCount, IReadOnlyCollection<PropertySummaryResponse> Items);
