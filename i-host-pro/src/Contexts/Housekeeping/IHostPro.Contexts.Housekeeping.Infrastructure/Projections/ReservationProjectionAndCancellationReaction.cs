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
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public ReservationProjectionAndCancellationReaction(
        HousekeepingDbContext dbContext,
        IHousekeepingTransactionExecutor executor,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _executor = executor;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Idempotent by construction — a redelivered <c>ReservationCreated</c>
    /// either finds no existing row (creates it) or an existing one (no-op).
    /// </summary>
    public Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
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
    /// </summary>
    public Task HandleAsync(ReservationCancelled @event, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
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
