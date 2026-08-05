using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IReservationConflictGuard"/>
/// <remarks>
/// <see cref="AcquirePropertyLockAsync"/> uses PostgreSQL's built-in
/// <c>pg_advisory_xact_lock(bigint)</c> — a core server function, no
/// extension required (Fase 3, Incremento 1 plan, item 7: "não usar...
/// extensão PostgreSQL nova sem necessidade") — combined with
/// <c>hashtextextended(text, bigint)</c> (also core, PostgreSQL 11+) to fold
/// the <c>(tenantId, propertyId)</c> pair into a single 64-bit lock key,
/// avoiding the collision risk of splitting two GUIDs across the two
/// separate 32-bit keys the <c>pg_advisory_xact_lock(int, int)</c> overload
/// would require. Transaction-scoped: released automatically at COMMIT/
/// ROLLBACK, never needs an explicit unlock call.
///
/// <see cref="HasConflictingReservationAsync"/> does not need
/// <c>tenantId</c> for the query itself — <see cref="ReservationsDbContext"/>'s
/// Global Query Filter already scopes it — but accepts it for symmetry with
/// <see cref="AcquirePropertyLockAsync"/>'s signature, where it IS required
/// (an advisory lock is a cluster-wide primitive, entirely outside RLS/the
/// Global Query Filter's reach).
/// </remarks>
public sealed class ReservationConflictGuard : IReservationConflictGuard
{
    private readonly ReservationsDbContext _dbContext;

    public ReservationConflictGuard(ReservationsDbContext dbContext) => _dbContext = dbContext;

    public async Task AcquirePropertyLockAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        var lockKey = $"reservations:{tenantId:D}:{propertyId:D}";

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", cancellationToken);
    }

    public async Task<bool> HasConflictingReservationAsync(
        Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt,
        Guid? excludeReservationId, CancellationToken cancellationToken)
    {
        // Npgsql's `timestamp with time zone` parameter binding rejects any
        // DateTimeOffset whose Offset isn't exactly zero — callers pass the
        // command's raw, possibly non-UTC value here (normalization to UTC
        // is otherwise only guaranteed once Reservation.Create/Reschedule
        // runs, which is AFTER this conflict check). Normalizing here keeps
        // that guarantee local to this query, without requiring every caller
        // to remember to normalize first.
        var normalizedCheckInAt = checkInAt.ToUniversalTime();
        var normalizedCheckOutAt = checkOutAt.ToUniversalTime();

        var query = _dbContext.Reservations
            .Where(r => r.PropertyId == propertyId)
            .Where(r => r.Status == ReservationStatus.Confirmed)
            .Where(r => r.CheckInAt < normalizedCheckOutAt && r.CheckOutAt > normalizedCheckInAt);

        if (excludeReservationId is Guid excludeId)
            query = query.Where(r => r.Id != excludeId);

        return await query.AnyAsync(cancellationToken);
    }
}
