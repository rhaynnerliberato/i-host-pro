namespace IHostPro.Contexts.Identity.Application.Catalog;

/// <summary>
/// A permission from the platform's fixed catalog, for administrative
/// listing (Incremento 3, Checkpoint 3) — the four fields that actually exist
/// on the persisted <c>Permission</c> entity, nothing invented (no
/// description, display name, or other metadata the model does not have).
/// <see cref="Scope"/> is null when the permission has none (e.g.
/// <c>"USERS:MANAGE"</c>), matching the persisted column.
/// </summary>
public sealed record CatalogPermission(string Code, string Resource, string Action, string? Scope);
