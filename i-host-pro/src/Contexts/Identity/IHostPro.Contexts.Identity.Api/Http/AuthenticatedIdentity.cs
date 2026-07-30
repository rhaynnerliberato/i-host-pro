namespace IHostPro.Contexts.Identity.Api.Http;

/// <summary>
/// The three identifiers every self-service/authenticated-only action needs,
/// read from the caller's own validated access token claims (Incremento 3,
/// Checkpoint 4) — never from a request body, route or query string. See
/// <see cref="AuthenticatedIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedIdentity(Guid UserId, Guid TenantId, Guid SessionId);
