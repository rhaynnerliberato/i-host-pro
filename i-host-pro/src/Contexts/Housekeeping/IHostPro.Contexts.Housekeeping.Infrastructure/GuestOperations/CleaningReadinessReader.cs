using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.GuestOperations;

/// <inheritdoc cref="ICleaningReadinessReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="ICleaningReadinessReader"/> (Fase 10, Checkpoint 3 — ADR-024
/// amendment, synchronous exception #8) — lives in
/// <c>Housekeeping.Infrastructure</c>, the one layer allowed to touch
/// <see cref="HousekeepingDbContext"/> directly. Mirrors
/// <c>Reservations.Infrastructure.GuestOperations.ReservationScheduleReader</c>'s
/// own structural precedent exactly: its own short-lived, read-only,
/// tenant-scoped transaction via <see cref="TenantAwareTransactionScope"/>, a
/// throwaway local <see cref="TenantContext"/>, no cache, no mutation.
/// </remarks>
public sealed class CleaningReadinessReader : ICleaningReadinessReader
{
    private const string Purpose = "guest_operations_early_checkin_readiness";
    private const string Caller = "GuestOperations";

    private readonly HousekeepingDbContext _dbContext;
    private readonly ILogger<CleaningReadinessReader> _logger;

    public CleaningReadinessReader(HousekeepingDbContext dbContext, ILogger<CleaningReadinessReader> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<bool> IsCleaningCompletedAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var isCompleted = await _dbContext.Cleanings
            .AsNoTracking()
            .Where(c => c.ReservationId == reservationId)
            .AnyAsync(c => c.Status == CleaningStatus.Completed, cancellationToken);

        _logger.LogInformation(
            "Cleaning readiness read for {Purpose} by {Caller}: tenant {TenantId} reservation {ReservationId} — result {Result}",
            Purpose, Caller, tenantId, reservationId, isCompleted ? "Ready" : "NotReady");

        return isCompleted;
    }
}
