using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IScheduleReader"/>
/// <remarks>
/// Reads from two tables of this SAME schema — <c>Reservations</c> (this
/// Bounded Context's own aggregate) and <c>CleaningScheduleProjection</c>
/// (the local, event-fed read-model of Housekeeping's own Cleaning — see
/// that entity's own doc comment); never a cross-context query, never
/// <c>HousekeepingDbContext</c>. Both are <c>ITenantOwned</c>, so
/// <c>ReservationsDbContext</c>'s Global Query Filter already scopes both
/// queries to the current tenant — mirrors <c>ReservationReader</c>. Relies
/// on the ambient tenant-aware transaction <c>TenantTransactionBehavior&lt;,&gt;</c>
/// already opens around the whole query-handler call (RLS's
/// <c>app.tenant_id</c> included) — no scope-opening needed here.
///
/// A Cleaning with <c>ScheduledAtUtc == null</c> is excluded — it "does not
/// participate in schedule-based ordering" (<c>Cleaning.ScheduledAtUtc</c>'s
/// own doc comment) and therefore has no <see cref="ScheduleItemResult.StartAtUtc"/>
/// to report. Cleaning items never carry an <c>EndAtUtc</c> — no scheduled
/// duration exists in the domain (Checkpoint 1, item 17 of the approval:
/// "não inventar duração de Cleaning").
///
/// A <paramref name="housekeeperUserId"/> filter excludes every Reservation
/// (Reservations carry no housekeeper) — narrowing to Cleanings only,
/// regardless of <paramref name="eventType"/>.
/// </remarks>
public sealed class ScheduleReader : IScheduleReader
{
    private readonly ReservationsDbContext _dbContext;

    public ScheduleReader(ReservationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<ScheduleItemResult>> ListAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? propertyId, Guid? housekeeperUserId, string? eventType,
        CancellationToken cancellationToken)
    {
        var includeReservations = eventType is null or "Reservation";
        var includeCleanings = eventType is null or "Cleaning";

        var items = new List<ScheduleItemResult>();

        if (includeReservations && housekeeperUserId is null)
        {
            var reservationsQuery = _dbContext.Reservations.AsNoTracking()
                .Where(r => r.CheckOutAt > from && r.CheckInAt < to);

            if (propertyId is Guid effectivePropertyId)
                reservationsQuery = reservationsQuery.Where(r => r.PropertyId == effectivePropertyId);

            var reservations = await reservationsQuery.ToListAsync(cancellationToken);

            items.AddRange(reservations.Select(r => new ScheduleItemResult(
                r.Id, "Reservation", r.PropertyId, r.CheckInAt, r.CheckOutAt,
                ReservationStatusCodeMapper.ToCode(r.Status), HousekeeperUserId: null, r.Id)));
        }

        if (includeCleanings)
        {
            var cleaningsQuery = _dbContext.CleaningScheduleProjection.AsNoTracking()
                .Where(c => c.ScheduledAtUtc != null && c.ScheduledAtUtc >= from && c.ScheduledAtUtc < to);

            if (propertyId is Guid effectivePropertyId)
                cleaningsQuery = cleaningsQuery.Where(c => c.PropertyId == effectivePropertyId);

            if (housekeeperUserId is Guid effectiveHousekeeperUserId)
                cleaningsQuery = cleaningsQuery.Where(c => c.AssignedHousekeeperUserId == effectiveHousekeeperUserId);

            var cleanings = await cleaningsQuery.ToListAsync(cancellationToken);

            items.AddRange(cleanings.Select(c => new ScheduleItemResult(
                c.CleaningId, "Cleaning", c.PropertyId, c.ScheduledAtUtc!.Value, EndAtUtc: null,
                c.Status, c.AssignedHousekeeperUserId, c.CleaningId)));
        }

        return items
            .OrderBy(i => i.StartAtUtc)
            .ThenBy(i => i.Id)
            .ToArray();
    }
}
