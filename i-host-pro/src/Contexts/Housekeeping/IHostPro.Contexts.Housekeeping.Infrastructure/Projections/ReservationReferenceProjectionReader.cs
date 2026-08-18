using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <inheritdoc cref="IReservationReferenceProjection"/>
/// <remarks>
/// Opens its own short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> — see
/// <c>PropertyReferenceProjectionReader</c>'s own doc comment for the full
/// reasoning (RLS's <c>SET LOCAL app.tenant_id</c> is never satisfied by the
/// EF Core Global Query Filter alone).
/// </remarks>
public sealed class ReservationReferenceProjectionReader : IReservationReferenceProjection
{
    private readonly HousekeepingDbContext _dbContext;

    public ReservationReferenceProjectionReader(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> ExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        return await _dbContext.ReservationProjection
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId, cancellationToken);
    }

    public async Task<bool> IsCancelledAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        return await _dbContext.ReservationProjection
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId && r.IsCancelled, cancellationToken);
    }
}
