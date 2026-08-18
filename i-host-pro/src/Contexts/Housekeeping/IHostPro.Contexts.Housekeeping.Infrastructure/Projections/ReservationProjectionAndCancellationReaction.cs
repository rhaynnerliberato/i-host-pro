using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="ReservationProjectionEntry"/> in sync with Reservations'
/// own <c>ReservationCreated</c>/<c>ReservationCancelled</c> (Fase 6,
/// Incremento 1, Checkpoint 0/3 gate) and, for <c>ReservationCancelled</c>,
/// automatically cancels any linked Cleaning still in a cancelable status —
/// the business logic behind two separate, minimal Wolverine adapters
/// (<c>ReservationCreatedHandler</c>/<c>ReservationCancelledHandler</c>),
/// never referencing Wolverine itself. This class name deliberately does NOT
/// end in "Handler" — see <c>PropertyProjectionSynchronizer</c>'s own doc
/// comment for why.
/// </summary>
public sealed class ReservationProjectionAndCancellationReaction :
    IIntegrationEventHandler<ReservationCreated>,
    IIntegrationEventHandler<ReservationCancelled>
{
    private readonly HousekeepingDbContext _dbContext;
    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly IReservationCancellationGuard _cancellationGuard;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public ReservationProjectionAndCancellationReaction(
        HousekeepingDbContext dbContext,
        IHousekeepingTransactionExecutor executor,
        IReservationCancellationGuard cancellationGuard,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _executor = executor;
        _cancellationGuard = cancellationGuard;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Idempotent by construction — a redelivered <c>ReservationCreated</c>
    /// either finds no existing row (creates it) or an existing one (no-op).
    /// Acquires <see cref="IReservationCancellationGuard"/>'s lock FIRST
    /// (Fase 8, Checkpoint 1.1) — the same per-(tenantId, reservationId)
    /// serialization point <c>CreateCleaningForReservationCommandHandler</c>
    /// and this class's own <see cref="HandleAsync(ReservationCancelled, CancellationToken)"/>
    /// use, so a <c>ReservationCancelled</c> processed out of order (before
    /// this event, or concurrently with it) can never be overwritten: this
    /// method never touches <see cref="ReservationProjectionEntry.IsCancelled"/>
    /// at all, so there is nothing here that could reset it back to
    /// <c>false</c> even without the lock — the lock's purpose here is only
    /// to prevent a duplicate-key race on the row's own creation against a
    /// concurrent <c>ReservationCancelled</c>/command doing the same
    /// insert-if-absent for the same reservation.
    /// </summary>
    public Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
            await _cancellationGuard.AcquireLockAsync(@event.TenantId, @event.ReservationId, cancellationToken);

            var exists = await _dbContext.ReservationProjection.AnyAsync(
                r => r.TenantId == @event.TenantId && r.ReservationId == @event.ReservationId, cancellationToken);

            if (!exists)
                _dbContext.ReservationProjection.Add(new ReservationProjectionEntry(@event.TenantId, @event.ReservationId));

            return true;
        }, cancellationToken);

    /// <summary>
    /// Cancels every Cleaning linked to this reservation that is still
    /// <see cref="CleaningStatus.Pending"/>/<see cref="CleaningStatus.Assigned"/>
    /// (Checkpoint 0/3 approved decision) — never
    /// <see cref="CleaningStatus.Completed"/> or one already
    /// <see cref="CleaningStatus.Cancelled"/>. Idempotent and safe to
    /// redeliver: on a second delivery, the query below matches zero rows
    /// (the Cleaning is already <c>Cancelled</c>, no longer in a cancelable
    /// status), so nothing happens and no duplicate <c>CleaningCancelled</c>
    /// is published.
    ///
    /// Acquires <see cref="IReservationCancellationGuard"/>'s lock FIRST
    /// (Fase 8, Checkpoint 1.1) — the real fix for the CP1 cancellation race:
    /// under this lock, this method's own linked-Cleaning scan below can
    /// never run concurrently with <c>CreateCleaningForReservationCommandHandler</c>'s
    /// own lock-protected creation for the same reservation. Combined with
    /// <see cref="ReservationProjectionEntry.MarkCancelled"/> being
    /// monotonic (never resets <c>false</c>), this guarantees the invariant:
    /// once every in-flight message for a reservation has been processed, a
    /// cancelled Reservation can never have an ACTIVE automated Cleaning —
    /// either none was ever created, or the one that was gets cancelled
    /// right here as soon as it becomes visible.
    /// </summary>
    public Task HandleAsync(ReservationCancelled @event, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
            await _cancellationGuard.AcquireLockAsync(@event.TenantId, @event.ReservationId, cancellationToken);

            var projectionEntry = await _dbContext.ReservationProjection.FirstOrDefaultAsync(
                r => r.TenantId == @event.TenantId && r.ReservationId == @event.ReservationId, cancellationToken);

            if (projectionEntry is null)
            {
                projectionEntry = new ReservationProjectionEntry(@event.TenantId, @event.ReservationId);
                _dbContext.ReservationProjection.Add(projectionEntry);
            }

            projectionEntry.MarkCancelled();

            var linkedCleanings = await _dbContext.Cleanings
                .Where(c => c.ReservationId == @event.ReservationId
                    && (c.Status == CleaningStatus.Pending || c.Status == CleaningStatus.Assigned))
                .ToListAsync(cancellationToken);

            var now = _timeProvider.GetUtcNow();

            foreach (var cleaning in linkedCleanings)
            {
                cleaning.Cancel(now);

                _auditWriter.Record(CleaningAuditEntry.Create(
                    Guid.NewGuid(), @event.TenantId, cleaning.CreatedByUserId, "Cleaning", cleaning.Id,
                    "cleaning_cancelled_by_reservation_cancellation", ["status"], now));

                _eventCollector.Enqueue(new CleaningCancelled
                {
                    TenantId = @event.TenantId,
                    AggregateId = cleaning.Id,
                    AggregateType = "Cleaning",
                    CorrelationId = @event.CorrelationId,
                    CausationId = @event.EventId,
                    ActorType = "System",
                    ActorId = null,
                    CleaningId = cleaning.Id,
                });
            }

            return true;
        }, cancellationToken);
}
