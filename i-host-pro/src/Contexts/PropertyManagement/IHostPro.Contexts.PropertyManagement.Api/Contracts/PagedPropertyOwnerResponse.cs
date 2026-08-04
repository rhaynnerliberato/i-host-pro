namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record PagedPropertyOwnerResponse(int Page, int PageSize, int TotalCount, IReadOnlyCollection<PropertyOwnerResponse> Items);
