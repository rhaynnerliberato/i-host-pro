using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Revokes the caller's current session (Incremento 2 plan, Etapa 8).
/// <see cref="TenantId"/>, <see cref="UserId"/> and <see cref="SessionId"/>
/// come exclusively from the authenticated access token's claims — a future
/// controller constructs this command from <c>ClaimsPrincipal</c>, never
/// from the request body. There is deliberately no constructor parameter
/// through which a client could supply its own tenant/user/session id, and
/// no refresh token parameter at all: logout revokes the session identified
/// by the token the caller is already authenticated with, nothing else.
///
/// Does NOT implement <see cref="IBootstrapRequest"/>: unlike login/refresh,
/// the caller is already authenticated, so the tenant is resolved the normal
/// way, from the JWT claim, via <c>TenantTransactionBehavior</c>.
///
/// No sensitive value here — the default record <see cref="ToString"/> is
/// safe to keep (only GUIDs, no secret).
/// </summary>
public sealed record LogoutCommand(Guid TenantId, Guid UserId, Guid SessionId) : ICommand;
