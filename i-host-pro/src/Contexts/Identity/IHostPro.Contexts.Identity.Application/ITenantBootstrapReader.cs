namespace IHostPro.Contexts.Identity.Application;

/// <summary>Minimal, non-tenant-owned tenant data needed to bootstrap authentication.</summary>
public sealed record ActiveTenant(Guid Id, string Slug);

/// <summary>
/// Resolves a tenant by either of its two public identifiers — slug (used at
/// login) or id (embedded in the opaque refresh token, used at token refresh)
/// — without ever touching a tenant-owned table. The only data source it may
/// query is the platform-level `tenants` table, which has no Row-Level
/// Security (Incremento 1 plan, Section 1 and Section 8). Implemented by
/// Identity.Infrastructure; consumed by the
/// <c>ITenantBootstrapResolver&lt;TRequest&gt;</c> implementations that back
/// future bootstrap requests (login, refresh — Incremento 2).
/// </summary>
public interface ITenantBootstrapReader
{
    Task<ActiveTenant?> GetActiveTenantBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<ActiveTenant?> GetActiveTenantByIdAsync(Guid tenantId, CancellationToken cancellationToken);
}
