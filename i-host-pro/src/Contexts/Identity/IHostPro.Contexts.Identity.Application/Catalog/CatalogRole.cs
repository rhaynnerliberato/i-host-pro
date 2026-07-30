namespace IHostPro.Contexts.Identity.Application.Catalog;

/// <summary>
/// A role from the platform's fixed catalog, for administrative listing
/// (Incremento 3, Checkpoint 3). <see cref="Code"/> is the stable technical
/// code (e.g. <c>"ADMIN"</c>); <see cref="Name"/> is the persisted display
/// name (<c>Role.DisplayName</c>) — never a value invented here.
/// <see cref="PermissionCodes"/> is distinct and ordered using ordinal
/// comparison; empty when the role grants nothing, never omitted or null.
/// </summary>
public sealed record CatalogRole(string Code, string Name, IReadOnlyCollection<string> PermissionCodes);
