using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Revokes one of the authenticated caller's own sessions (Incremento 3,
/// Checkpoint 4). <see cref="TenantId"/>/<see cref="UserId"/> come exclusively
/// from the authenticated access token's <c>tenant_id</c>/<c>sub</c> claims —
/// a controller builds this from <c>ClaimsPrincipal</c>, never from the
/// request body. <see cref="SessionId"/> is the one genuinely
/// client-supplied value (the <c>{sessionId:guid}</c> route parameter) — it
/// identifies the RESOURCE being acted on, not the actor, and the handler
/// must verify it actually belongs to <see cref="UserId"/>/<see cref="TenantId"/>
/// before revoking anything (see <see cref="RevokeOwnSessionCommandHandler"/>).
///
/// Mirrors <c>LogoutCommand</c>'s shape and non-bootstrap reasoning exactly —
/// the caller is already authenticated, so the tenant is resolved the normal
/// way, from the JWT claim, via <c>TenantTransactionBehavior</c>/
/// <c>RevokeOwnSessionTenantAwareBehavior</c>.
/// </summary>
public sealed record RevokeOwnSessionCommand(Guid TenantId, Guid UserId, Guid SessionId) : ICommand;
