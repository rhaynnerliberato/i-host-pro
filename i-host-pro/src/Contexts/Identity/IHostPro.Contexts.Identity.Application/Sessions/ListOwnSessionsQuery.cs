using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Lists the authenticated caller's own active sessions (Incremento 3,
/// Checkpoint 4). <see cref="UserId"/>/<see cref="CurrentSessionId"/> come
/// exclusively from the authenticated access token's <c>sub</c>/<c>session_id</c>
/// claims — a controller builds this from <c>ClaimsPrincipal</c>, never from
/// the request body/route/query string. <see cref="CurrentSessionId"/> is
/// used only to compute <see cref="OwnSessionResult.IsCurrent"/> by
/// comparison, never as a filter. No tenant parameter is needed — resolved
/// the normal way, from the JWT claim, via <c>TenantTransactionBehavior</c>.
/// </summary>
public sealed record ListOwnSessionsQuery(Guid UserId, Guid CurrentSessionId) : IQuery<IReadOnlyCollection<OwnSessionResult>>;
