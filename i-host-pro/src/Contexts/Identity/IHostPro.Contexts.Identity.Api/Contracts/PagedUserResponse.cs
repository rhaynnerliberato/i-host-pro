namespace IHostPro.Contexts.Identity.Api.Contracts;

public sealed record PagedUserResponse(int Page, int PageSize, int TotalCount, IReadOnlyCollection<UserResponse> Items);
