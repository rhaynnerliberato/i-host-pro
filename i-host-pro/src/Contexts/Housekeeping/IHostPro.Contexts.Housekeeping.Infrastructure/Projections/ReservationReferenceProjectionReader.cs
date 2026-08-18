using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <inheritdoc cref="IReservationReferenceProjection"/>
/// <remarks>
/// <see cref="ExistsAsync"/> opens its own short-lived, read-only,
/// tenant-scoped transaction via <see cref="TenantAwareTransactionScope"/> —
/// see <c>PropertyReferenceProjectionReader</c>'s own doc comment for the
/// full reasoning (RLS's <c>SET LOCAL app.tenant_id</c> is never satisfied by
/// the EF Core Global Query Filter alone). <see cref="EnsureExistsAsync"/>
/// and <see cref="IsCancelledAsync"/> deliberately do NOT (Fase 8, Checkpoint
/// 1.1) — both are only ever called from within an already-open ambient
/// transaction opened by <c>IHousekeepingTransactionExecutor</c>, immediately
/// after <see cref="IReservationCancellationGuard.AcquireLockAsync"/> for the
/// same reservation; opening a nested transaction there would throw
/// <c>NestedUnitOfWorkException</c>.
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

    /// <inheritdoc cref="IReservationReferenceProjection.EnsureExistsAsync"/>
    public async Task EnsureExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.ReservationProjection
            .AnyAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId, cancellationToken);

        if (!exists)
            _dbContext.ReservationProjection.Add(new ReservationProjectionEntry(tenantId, reservationId));
    }

    /// <inheritdoc cref="IReservationReferenceProjection.IsCancelledAsync"/>
    public async Task<bool> IsCancelledAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.ReservationProjection
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId && r.IsCancelled, cancellationToken);
}
