using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Multitenancy;

/// <inheritdoc cref="ITenantBootstrapReader"/>
public sealed class TenantBootstrapReader : ITenantBootstrapReader
{
    private readonly IdentityDbContext _dbContext;

    public TenantBootstrapReader(IdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ActiveTenant?> GetActiveTenantBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        TenantSlug parsedSlug;
        try
        {
            parsedSlug = TenantSlug.Create(slug);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var tenant = await _dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Slug == parsedSlug && t.Status == TenantStatus.Active, cancellationToken);

        return tenant is null ? null : new ActiveTenant(tenant.Id, tenant.Slug.Value);
    }

    public async Task<ActiveTenant?> GetActiveTenantByIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);

        return tenant is null ? null : new ActiveTenant(tenant.Id, tenant.Slug.Value);
    }
}
