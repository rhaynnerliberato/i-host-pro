using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Profile;

/// <summary>
/// Reads the authenticated caller's own profile (Incremento 3, Checkpoint 4).
/// <see cref="UserId"/> comes exclusively from the authenticated access
/// token's <c>sub</c> claim — a controller builds this from
/// <c>ClaimsPrincipal</c>, never from the request body/route/query string;
/// there is no way for a client to request another user's profile through
/// this query. The tenant is resolved the normal way, from the JWT claim, via
/// <c>TenantTransactionBehavior</c> — no tenant parameter is needed here.
/// </summary>
public sealed record GetOwnProfileQuery(Guid UserId) : IQuery<OwnProfileResult>;
