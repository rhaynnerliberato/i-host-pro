namespace IHostPro.Contexts.Identity.Infrastructure.Authorization;

/// <summary>
/// Controls how long <see cref="RolePermissionCache"/> (Incremento 3,
/// Checkpoint 2) trusts a previously-loaded role's permission set before
/// re-reading PostgreSQL. The permission catalog is platform-fixed in this
/// phase (Documento 09 §18 — no endpoint can change it yet), so this TTL only
/// bounds how quickly a future manual catalog change would become visible;
/// it never risks serving stale data across a real business change, because
/// none is possible through the application yet.
/// </summary>
public sealed class PermissionCacheOptions
{
    public const string SectionName = "Identity:PermissionCache";

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);
}
