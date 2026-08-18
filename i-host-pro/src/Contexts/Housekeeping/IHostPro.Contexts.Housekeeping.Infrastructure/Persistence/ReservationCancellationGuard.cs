using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="IReservationCancellationGuard"/>
/// <remarks>
/// Uses PostgreSQL's built-in <c>pg_advisory_xact_lock(bigint)</c> combined
/// with <c>hashtextextended(text, bigint)</c> — the same core-server,
/// no-extension-required primitives <c>ReservationConflictGuard</c> and
/// <c>LastAdministratorGuard</c> already use for their own concurrency
/// guards. The lock key is namespaced with both this context's name and the
/// entity kind (<c>"housekeeping:reservation:..."</c>) so it can never
/// collide, in PostgreSQL's single global advisory-lock numeric space, with
/// Reservations' own <c>"reservations:{tenantId}:{propertyId}"</c> keys or
/// Identity's <c>"ihostpro:identity:admin-guard:{tenantId}"</c> keys.
///
/// Raw SQL issued through EF Core always executes on the DbContext's current
/// connection/transaction — this naturally joins whatever
/// <c>TenantAwareTransactionScope</c> already opened (see
/// <c>LastAdministratorGuard</c>'s own doc comment for the same reasoning).
/// </remarks>
public sealed class ReservationCancellationGuard : IReservationCancellationGuard
{
    private readonly HousekeepingDbContext _dbContext;

    public ReservationCancellationGuard(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task AcquireLockAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var lockKey = $"housekeeping:reservation:{tenantId:D}:{reservationId:D}";

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", cancellationToken);
    }
}
