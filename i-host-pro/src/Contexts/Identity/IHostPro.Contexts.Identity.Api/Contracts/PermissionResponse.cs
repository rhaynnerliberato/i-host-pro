namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public response body for one permission in <c>GET /api/v1/permissions</c>
/// (Incremento 3, Checkpoint 3) — this project's own DTO, mapped from the
/// Application-layer <c>CatalogPermission</c>, never that type serialized
/// directly. Property names are camelCase on the wire via the host's default
/// JSON naming policy.
/// </summary>
public sealed record PermissionResponse(string Code, string Resource, string Action, string? Scope);
