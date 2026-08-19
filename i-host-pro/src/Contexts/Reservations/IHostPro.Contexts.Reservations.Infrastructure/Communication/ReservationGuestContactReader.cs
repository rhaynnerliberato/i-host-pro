using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Infrastructure.Communication;

/// <inheritdoc cref="IReservationGuestContactReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IReservationGuestContactReader"/> (Fase 9, Checkpoint 1 —
/// ADR-019) — lives in <c>Reservations.Infrastructure</c>, the one layer
/// allowed to touch <see cref="ReservationsDbContext"/> directly. Opens its
/// own short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> using a throwaway local
/// <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> — mirrors
/// <c>PropertyManagement.Infrastructure.Reservations.PropertyReservationEligibilityReader"/>'s
/// own reasoning exactly (ADR-014's structural precedent). No cache, no
/// mutation.
///
/// Every call — found or not found — emits exactly one structured, PII-safe
/// audit log entry (ADR-019, item 11): <c>TenantId</c>, <c>ReservationId</c>,
/// <c>Purpose</c>, <c>Caller</c>, <c>Result</c> — the phone number itself is
/// NEVER a logged value, in either branch.
/// </remarks>
public sealed class ReservationGuestContactReader : IReservationGuestContactReader
{
    private const string Purpose = "communication_delivery";
    private const string Caller = "Communication";

    private readonly ReservationsDbContext _dbContext;
    private readonly ILogger<ReservationGuestContactReader> _logger;

    public ReservationGuestContactReader(ReservationsDbContext dbContext, ILogger<ReservationGuestContactReader> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ReservationGuestContact?> GetGuestContactAsync(
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
                "Guest contact read for {Purpose} by {Caller}: tenant {TenantId} reservation {ReservationId} — result {Result}",
                Purpose, Caller, tenantId, reservationId, "NotFound");
            return null;
        }

        _logger.LogInformation(
            "Guest contact read for {Purpose} by {Caller}: tenant {TenantId} reservation {ReservationId} — result {Result}",
            Purpose, Caller, tenantId, reservationId, "Found");

        return new ReservationGuestContact(reservation.Id, reservation.GuestPhone);
    }
}
