namespace IHostPro.Contexts.PropertyManagement.Api.Http;

/// <summary>
/// The two identifiers every Property Management action needs, read from
/// the caller's own validated access token claims (Checkpoint 2 plan, item
/// 3) — never from a request body, route or query string. Deliberately
/// narrower than Identity's own <c>AuthenticatedIdentity</c> (no
/// <c>SessionId</c>): Property Management has no session-scoped use case.
/// See <see cref="PropertyManagementIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedPropertyManagementIdentity(Guid UserId, Guid TenantId);
