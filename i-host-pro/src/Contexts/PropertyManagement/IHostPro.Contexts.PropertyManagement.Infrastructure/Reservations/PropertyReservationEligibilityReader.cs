using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Reservations;

/// <inheritdoc cref="IPropertyReservationEligibilityReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IPropertyReservationEligibilityReader"/> (Fase 3, Incremento 1
/// plan, item 4) — lives in <c>PropertyManagement.Infrastructure</c>, the
/// one layer allowed to touch <see cref="PropertyManagementDbContext"/>
/// directly. Opens its own short-lived, read-only, tenant-scoped
/// transaction via <see cref="TenantAwareTransactionScope"/> using a
/// throwaway local <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> — mirrors
/// <c>Identity.Infrastructure.Authorization.IdentityUserEligibilityReader</c>'s
/// own reasoning exactly: this scope exists purely to satisfy PostgreSQL's
/// OWN Row-Level Security enforcement (<c>SET LOCAL app.tenant_id</c>), a
/// second, independent layer that does not consult EF Core's Global Query
/// Filter at all. No cache, no audit, no event, no mutation.
/// </remarks>
public sealed class PropertyReservationEligibilityReader : IPropertyReservationEligibilityReader
{
    private readonly PropertyManagementDbContext _dbContext;

    public PropertyReservationEligibilityReader(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<PropertyReservationEligibility?> GetAsync(
        Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var property = await _dbContext.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

        if (property is null)
            return null;

        return new PropertyReservationEligibility(
            property.Id, property.Status == PropertyStatus.Active, property.Capacity);
    }
}
