using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <inheritdoc cref="IPropertyReferenceProjection"/>
/// <remarks>
/// Opens its own short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> using a throwaway local
/// <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> — mirrors
/// <c>PropertyReservationEligibilityReader</c>'s/
/// <c>IdentityUserEligibilityReader</c>'s own reasoning exactly: this scope
/// exists purely to satisfy PostgreSQL's OWN Row-Level Security enforcement
/// (<c>SET LOCAL app.tenant_id</c>), a second, independent layer that does
/// not consult EF Core's Global Query Filter at all. Without it, this query
/// runs with no <c>app.tenant_id</c> set and FORCE ROW LEVEL SECURITY fails
/// closed to zero rows regardless of the filter above matching.
/// </remarks>
public sealed class PropertyReferenceProjectionReader : IPropertyReferenceProjection
{
    private readonly HousekeepingDbContext _dbContext;

    public PropertyReferenceProjectionReader(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> IsKnownActivePropertyAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        return await _dbContext.PropertyProjection
            .AsNoTracking()
            .AnyAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId && p.IsActive, cancellationToken);
    }
}
