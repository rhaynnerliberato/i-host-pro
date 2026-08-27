using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Communication;

/// <inheritdoc cref="IFrontDeskContactReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IFrontDeskContactReader"/> (Fase 10, Checkpoint 4 — ADR-026)
/// — lives in <c>PropertyManagement.Infrastructure</c>, the one layer
/// allowed to touch <see cref="PropertyManagementDbContext"/> directly.
/// Opens its own short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> using a throwaway local
/// <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> — mirrors
/// <see cref="Reservations.PropertyReservationEligibilityReader"/>'s own
/// reasoning exactly (ADR-014's structural precedent). No cache, no audit,
/// no event, no mutation. Property/Condominium/FrontDeskContact resolution
/// is a single query, entirely inside PropertyManagement — Communication
/// never needs to know CondominiumId exists.
/// </remarks>
public sealed class FrontDeskContactReader : IFrontDeskContactReader
{
    private readonly PropertyManagementDbContext _dbContext;

    public FrontDeskContactReader(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<FrontDeskContactReadResult?> GetActiveByPropertyIdAsync(
        Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var property = await _dbContext.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

        if (property?.CondominiumId is not { } condominiumId)
            return null;

        var contact = await _dbContext.Set<FrontDeskContact>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CondominiumId == condominiumId && c.IsActive, cancellationToken);

        return contact is null
            ? null
            : new FrontDeskContactReadResult(contact.Id, contact.DisplayName, contact.PhoneNumber);
    }
}
