using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Infrastructure.GuestOperations;

/// <inheritdoc cref="IReservationScheduleReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IReservationScheduleReader"/> (Fase 10, Checkpoint 3 — ADR-024
/// amendment, synchronous exception #7) — lives in
/// <c>Reservations.Infrastructure</c>, the one layer allowed to touch
/// <see cref="ReservationsDbContext"/> directly. Mirrors
/// <c>ReservationGuestContactReader</c>'s own structural precedent exactly
/// (ADR-019): its own short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/>, a throwaway local
/// <see cref="TenantContext"/>, no cache, no mutation.
///
/// <see cref="HasConflictingReservationAsync"/> deliberately does NOT reuse
/// <see cref="IReservationConflictGuard"/> — that type is <c>internal</c> to
/// this Application project, requires an already-open write transaction
/// (its own <c>pg_advisory_xact_lock</c> is transaction-scoped), and is
/// unsuitable for a cross-context read-only query. This method mirrors only
/// the read-only PORTION of <c>IReservationConflictGuard.HasConflictingReservationAsync</c>'s
/// own query shape, minus the lock — the actual mutation
/// (<c>Reservation.Reschedule</c>, via <c>RescheduleReservationForEarlyCheckIn</c>/
/// <c>RescheduleReservationForLateCheckout</c>) still goes through
/// Reservations' own internal conflict guard for the real write.
/// </remarks>
public sealed class ReservationScheduleReader : IReservationScheduleReader
{
    private const string Purpose = "guest_operations_schedule_eligibility";
    private const string Caller = "GuestOperations";

    private readonly ReservationsDbContext _dbContext;
    private readonly ILogger<ReservationScheduleReader> _logger;

    public ReservationScheduleReader(ReservationsDbContext dbContext, ILogger<ReservationScheduleReader> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ReservationScheduleSnapshot?> GetScheduleAsync(
        Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var reservation = await _dbContext.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
        {
            _logger.LogInformation(
                "Schedule read for {Purpose} by {Caller}: tenant {TenantId} reservation {ReservationId} — result {Result}",
                Purpose, Caller, tenantId, reservationId, "NotFound");
            return null;
        }

        _logger.LogInformation(
            "Schedule read for {Purpose} by {Caller}: tenant {TenantId} reservation {ReservationId} — result {Result}",
            Purpose, Caller, tenantId, reservationId, "Found");

        return new ReservationScheduleSnapshot(
            ReservationStatusCodeMapper.ToCode(reservation.Status), reservation.CheckInAt, reservation.CheckOutAt);
    }

    public async Task<bool> HasConflictingReservationAsync(
        Guid tenantId, Guid reservationId, DateTimeOffset requestedCheckInAt, DateTimeOffset requestedCheckOutAt,
        CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var reservation = await _dbContext.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
            return false;

        var normalizedCheckInAt = requestedCheckInAt.ToUniversalTime();
        var normalizedCheckOutAt = requestedCheckOutAt.ToUniversalTime();

        return await _dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.PropertyId == reservation.PropertyId)
            .Where(r => r.Status == ReservationStatus.Confirmed)
            .Where(r => r.Id != reservationId)
            .Where(r => r.CheckInAt < normalizedCheckOutAt && r.CheckOutAt > normalizedCheckInAt)
            .AnyAsync(cancellationToken);
    }
}
