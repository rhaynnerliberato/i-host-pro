using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Lists users of the caller's tenant, paginated (Incremento 3, Checkpoint
/// 5). <see cref="Page"/>/<see cref="PageSize"/> are nullable — <c>null</c>
/// means "use the configured default" (<see cref="UserListingOptions"/>),
/// resolved by <see cref="ListUsersQueryHandler"/>, never hardcoded on this
/// record. No tenant parameter: resolved the normal way, from the JWT claim,
/// via <c>TenantTransactionBehavior</c>.
/// </summary>
public sealed record ListUsersQuery(int? Page, int? PageSize, string? Search, UserStatus? Status)
    : IQuery<PagedResult<UserResult>>;
