namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record PagedCondominiumResponse(int Page, int PageSize, int TotalCount, IReadOnlyCollection<CondominiumSummaryResponse> Items);
