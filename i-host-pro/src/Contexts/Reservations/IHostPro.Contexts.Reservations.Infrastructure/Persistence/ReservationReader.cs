using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IReservationReader"/>
/// <remarks>
/// Reads exclusively from <see cref="ReservationsDbContext"/> —
/// <c>Reservations</c> is <c>ITenantOwned</c>, so the ReservationsDbContext's Global
/// Query Filter already scopes every query here to the current tenant
/// (mirrors <c>PropertyReader</c>). <see cref="ListAsync"/>'s
/// <paramref name="from"/>/<paramref name="to"/> filters use the same
/// half-open interval-intersection shape as
/// <c>IReservationConflictGuard.HasConflictingReservationAsync</c> (start
/// inclusive, end exclusive), applied independently so a caller may supply
/// either bound alone.
///
/// <see cref="ListAsync"/>/<see cref="GetByIdAsync"/> rely on the ambient
/// tenant-aware transaction <c>TenantTransactionBehavior&lt;,&gt;</c> already
/// opens around the whole query-handler call (RLS's <c>app.tenant_id</c>
/// included) — no scope-opening needed here. <see cref="GetUpdateSnapshotAsync"/>
/// is different: <c>UpdateReservationCommand</c> deliberately gets NO
/// wrapping pipeline behavior (<c>ReservationsCommandDispatchExtensions</c>'
/// own doc comment), and this snapshot is read BEFORE
/// <c>IUpdateReservationExecutor</c> ever opens its own write transaction —
/// so it opens and closes its own short, read-only
/// <see cref="TenantAwareTransactionScope"/> to satisfy RLS. <see cref="GetCurrentXminAsync"/>
/// is called only from INSIDE that later write transaction, which already
/// has <c>app.tenant_id</c> set — opening a second scope there would throw
/// <c>NestedUnitOfWorkException</c>, so it queries directly.
/// </remarks>
public sealed class ReservationReader : IReservationReader
{
    public const int MaxPageSize = 100;

    private readonly ReservationsDbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public ReservationReader(ReservationsDbContext dbContext, ITenantContext tenantContext)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    public async Task<PagedResult<ReservationSummaryResult>> ListAsync(
        Guid? propertyId, string? status, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.Reservations.AsNoTracking();

        if (propertyId is Guid effectivePropertyId)
            query = query.Where(r => r.PropertyId == effectivePropertyId);

        if (status is not null)
        {
            var statusEnum = ReservationStatusCodeMapper.FromCode(status);
            query = query.Where(r => r.Status == statusEnum);
        }

        if (from is DateTimeOffset effectiveFrom)
            query = query.Where(r => r.CheckOutAt > effectiveFrom);

        if (to is DateTimeOffset effectiveTo)
            query = query.Where(r => r.CheckInAt < effectiveTo);

        var totalCount = await query.CountAsync(cancellationToken);

        var reservations = await query
            .OrderBy(r => r.CheckInAt)
            .ThenBy(r => r.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var items = reservations
            .Select(r => new ReservationSummaryResult(
                r.Id, r.PropertyId, r.GuestName, r.CheckInAt, r.CheckOutAt, r.GuestCount,
                ReservationStatusCodeMapper.ToCode(r.Status), r.CreatedAt, r.UpdatedAt))
            .ToArray();

        return new PagedResult<ReservationSummaryResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    public async Task<ReservationResult?> GetByIdAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await _dbContext.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
            return null;

        return new ReservationResult(
            reservation.Id,
            reservation.PropertyId,
            reservation.GuestName,
            reservation.GuestPhone,
            reservation.CheckInAt,
            reservation.CheckOutAt,
            reservation.GuestCount,
            ReservationStatusCodeMapper.ToCode(reservation.Status),
            reservation.CreatedAt,
            reservation.UpdatedAt);
    }

    public async Task<ReservationUpdateSnapshot?> GetUpdateSnapshotAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, _tenantContext, readOnly: true, cancellationToken);

        var snapshot = await _dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Select(r => new ReservationUpdateSnapshot(
                r.Id, r.PropertyId, r.CheckInAt, r.CheckOutAt, r.GuestCount, r.Status,
                EF.Property<uint>(r, "xmin")))
            .FirstOrDefaultAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<uint?> GetCurrentXminAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Select(r => (uint?)EF.Property<uint>(r, "xmin"))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid?> GetIdByExternalIdentityAsync(
        ReservationSource source, string externalReservationId, CancellationToken cancellationToken) =>
        await _dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.Source == source && r.ExternalReservationId == externalReservationId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
