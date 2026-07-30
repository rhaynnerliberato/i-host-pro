namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public response body for one role in <c>GET /api/v1/roles</c> (Incremento
/// 3, Checkpoint 3) — this project's own DTO, mapped from the
/// Application-layer <c>CatalogRole</c>, never that type serialized directly.
/// Property names are camelCase on the wire via the host's default JSON
/// naming policy.
/// </summary>
public sealed record RoleResponse(string Code, string Name, IReadOnlyCollection<string> PermissionCodes);
