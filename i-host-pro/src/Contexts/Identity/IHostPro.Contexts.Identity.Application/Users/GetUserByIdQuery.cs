using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Reads one user of the caller's tenant by id (Incremento 3, Checkpoint 5)
/// — <see cref="UserId"/> is the one genuinely client-supplied value here
/// (the <c>{userId:guid}</c> route parameter): this is an administrative
/// lookup of ANY user in the tenant, not a self-service query, so unlike
/// <c>GetOwnProfileQuery</c> it legitimately targets someone other than the
/// caller. Tenant isolation still applies — a user id from a different
/// tenant is indistinguishable from a nonexistent one (RLS/the Global Query
/// Filter on the query itself).
/// </summary>
public sealed record GetUserByIdQuery(Guid UserId) : IQuery<UserResult>;
