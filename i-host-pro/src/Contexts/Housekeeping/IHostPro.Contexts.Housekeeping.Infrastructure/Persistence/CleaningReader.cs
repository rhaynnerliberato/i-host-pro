using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="ICleaningReader"/>
/// <remarks>
/// Reads exclusively from <see cref="HousekeepingDbContext"/> —
/// <c>Cleaning</c> is <c>ITenantOwned</c>, so the Global Query Filter
/// already scopes every query here to the current tenant (mirrors
/// <c>ReservationReader</c>). Relies on the ambient tenant-aware transaction
/// <c>TenantTransactionBehavior&lt;,&gt;</c> already opens around the whole
/// query-handler call (RLS's <c>app.tenant_id</c> included) — no
/// scope-opening needed here.
/// </remarks>
public sealed class CleaningReader : ICleaningReader
{
    public const int MaxPageSize = 100;

    private readonly HousekeepingDbContext _dbContext;

    public CleaningReader(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<CleaningSummaryResult>> ListAsync(
        string? status, Guid? propertyId, Guid? assignedHousekeeperUserId,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.Cleanings.AsNoTracking();

        if (status is not null)
        {
            var statusEnum = CleaningStatusCodeMapper.FromCode(status);
            query = query.Where(c => c.Status == statusEnum);
        }

        if (propertyId is Guid effectivePropertyId)
            query = query.Where(c => c.PropertyId == effectivePropertyId);

        if (assignedHousekeeperUserId is Guid effectiveHousekeeperId)
            query = query.Where(c => c.AssignedHousekeeperUserId == effectiveHousekeeperId);

        var totalCount = await query.CountAsync(cancellationToken);

        var cleanings = await query
            .OrderBy(c => c.CreatedAtUtc)
            .ThenBy(c => c.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var items = cleanings
            .Select(c => new CleaningSummaryResult(
                c.Id, c.PropertyId, c.ReservationId, c.AssignedHousekeeperUserId,
                CleaningStatusCodeMapper.ToCode(c.Status), c.CreatedAtUtc, c.ScheduledAtUtc))
            .ToArray();

        return new PagedResult<CleaningSummaryResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    public async Task<CleaningResult?> GetByIdAsync(Guid cleaningId, CancellationToken cancellationToken)
    {
        var cleaning = await _dbContext.Cleanings
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cleaningId, cancellationToken);

        return cleaning is null ? null : ToResult(cleaning);
    }

    public async Task<PagedResult<CleaningSummaryResult>> ListForHousekeeperAsync(
        Guid housekeeperUserId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.Cleanings.AsNoTracking()
            .Where(c => c.AssignedHousekeeperUserId == housekeeperUserId);

        if (status is not null)
        {
            var statusEnum = CleaningStatusCodeMapper.FromCode(status);
            query = query.Where(c => c.Status == statusEnum);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var cleanings = await query
            .OrderBy(c => c.ScheduledAtUtc == null)
            .ThenBy(c => c.ScheduledAtUtc)
            .ThenBy(c => c.CreatedAtUtc)
            .ThenBy(c => c.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var items = cleanings
            .Select(c => new CleaningSummaryResult(
                c.Id, c.PropertyId, c.ReservationId, c.AssignedHousekeeperUserId,
                CleaningStatusCodeMapper.ToCode(c.Status), c.CreatedAtUtc, c.ScheduledAtUtc))
            .ToArray();

        return new PagedResult<CleaningSummaryResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    public async Task<CleaningResult?> GetByIdForHousekeeperAsync(
        Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken)
    {
        var cleaning = await _dbContext.Cleanings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Id == cleaningId && c.AssignedHousekeeperUserId == housekeeperUserId, cancellationToken);

        return cleaning is null ? null : ToResult(cleaning);
    }

    /// <inheritdoc cref="ICleaningReader.ExistsAutomatedForReservationAsync"/>
    public async Task<bool> ExistsAutomatedForReservationAsync(
        Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.Cleanings
            .AsNoTracking()
            .AnyAsync(
                c => c.TenantId == tenantId && c.ReservationId == reservationId && c.CreatedByUserId == null,
                cancellationToken);

    /// <inheritdoc cref="ICleaningReader.GetAutomatedCleaningIdByReservationIdAsync"/>
    public async Task<Guid?> GetAutomatedCleaningIdByReservationIdAsync(
        Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.Cleanings
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ReservationId == reservationId && c.CreatedByUserId == null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc cref="ICleaningReader.GetStatusByReservationIdAsync"/>
    public async Task<CleaningStatusResult?> GetStatusByReservationIdAsync(
        Guid reservationId, CancellationToken cancellationToken)
    {
        var cleaning = await _dbContext.Cleanings
            .AsNoTracking()
            .Where(c => c.ReservationId == reservationId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return cleaning is null
            ? null
            : new CleaningStatusResult(CleaningStatusCodeMapper.ToCode(cleaning.Status), cleaning.ScheduledAtUtc, cleaning.CompletedAtUtc);
    }

    private static CleaningResult ToResult(Domain.Cleaning cleaning) => new(
        cleaning.Id,
        cleaning.PropertyId,
        cleaning.ReservationId,
        cleaning.AssignedHousekeeperUserId,
        CleaningStatusCodeMapper.ToCode(cleaning.Status),
        cleaning.CreatedByUserId,
        cleaning.CreatedAtUtc,
        cleaning.ScheduledAtUtc,
        cleaning.StartedAtUtc,
        cleaning.InspectionStartedAtUtc,
        cleaning.CompletedAtUtc,
        cleaning.CancelledAtUtc);
}
