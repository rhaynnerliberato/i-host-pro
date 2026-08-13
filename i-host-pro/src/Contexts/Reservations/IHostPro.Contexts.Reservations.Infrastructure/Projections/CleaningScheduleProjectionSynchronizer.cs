using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="CleaningScheduleProjectionEntry"/> in sync with
/// Housekeeping's own Cleaning lifecycle events (Fase 7, Incremento 1 —
/// Agenda Foundation) — the business logic behind a set of separate,
/// minimal Wolverine adapters, never referencing Wolverine itself — mirrors
/// <c>PropertyProjectionSynchronizer</c>'s own separation exactly. This
/// class name deliberately does NOT end in "Handler" — Wolverine's own
/// naming-convention discovery would otherwise match this class too (same
/// double-discovery defect already found and avoided in Housekeeping).
///
/// Generalized in two stages: (1) Checkpoint 1's original gate —
/// <c>CleaningCreated</c> proven green through a real RabbitMQ/Worker/Postgres
/// transport test (<c>CleaningCreatedScheduleProjectionWorkerRoundTripTests</c>)
/// — then generalized to the six Cleaning events routed at that time; (2)
/// Checkpoint 1 CLOSURE (status-coverage gap fix, approved): every real
/// <c>Cleaning.Status</c> transition now publishes an event and is consumed
/// here — see Documento 07 §29.7 for the full transition→event→status
/// matrix. <c>CleaningNeedsHelp</c>/<c>CleaningNeedsMaterial</c> already
/// existed since Incremento 2A but were never routed by <c>IHostPro.Api</c>
/// at all (a real defect, fixed alongside this generalization — Documento 07
/// §29.4). <c>CleaningInTransit</c>/<c>CleaningInterrupted</c> are brand-new
/// events (Documento 07 §29.5-§29.6) for the two transitions that previously
/// published nothing.
///
/// <c>CleaningDelayed</c> remains the one Cleaning event deliberately NOT
/// consumed — it carries no field this projection displays and corresponds
/// to no <c>Cleaning.Status</c> transition (<c>ReportOwnCleaningDelayCommandHandler</c>
/// never calls a <c>Cleaning</c> transition method), so consuming it would
/// be a pure no-op (Documento 07 §29.8). Not inferred into a new
/// <c>ScheduledAtUtc</c> value.
///
/// Uses <see cref="IReservationsTransactionExecutor"/> for the
/// tenant-aware/RLS-protected write even though no Integration Event is
/// published here — the executor's outbox-drain/publish step is a harmless
/// no-op when nothing was staged (mirrors <c>PropertyProjectionSynchronizer</c>).
/// </summary>
public sealed class CleaningScheduleProjectionSynchronizer :
    IIntegrationEventHandler<CleaningCreated>,
    IIntegrationEventHandler<CleaningAssigned>,
    IIntegrationEventHandler<CleaningInTransit>,
    IIntegrationEventHandler<CleaningStarted>,
    IIntegrationEventHandler<CleaningInspectionStarted>,
    IIntegrationEventHandler<CleaningCompleted>,
    IIntegrationEventHandler<CleaningInterrupted>,
    IIntegrationEventHandler<CleaningNeedsHelp>,
    IIntegrationEventHandler<CleaningNeedsMaterial>,
    IIntegrationEventHandler<CleaningCancelled>
{
    private readonly ReservationsDbContext _dbContext;
    private readonly IReservationsTransactionExecutor _executor;

    public CleaningScheduleProjectionSynchronizer(ReservationsDbContext dbContext, IReservationsTransactionExecutor executor)
    {
        _dbContext = dbContext;
        _executor = executor;
    }

    /// <summary>
    /// Idempotent by construction: a redelivered <c>CleaningCreated</c>
    /// finds the row already present (harmless no-op) instead of inserting
    /// a duplicate.
    /// </summary>
    public Task HandleAsync(CleaningCreated @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var exists = await _dbContext.CleaningScheduleProjection
                .AnyAsync(c => c.TenantId == @event.TenantId && c.CleaningId == @event.CleaningId, cancellationToken);

            if (!exists)
            {
                _dbContext.CleaningScheduleProjection.Add(new CleaningScheduleProjectionEntry(
                    @event.TenantId, @event.CleaningId, @event.PropertyId, @event.Status, @event.ScheduledAtUtc));
            }

            return true;
        }, cancellationToken);

    public Task HandleAsync(CleaningAssigned @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Assigned", entry => entry.SetAssignedHousekeeper(@event.HousekeeperUserId), cancellationToken);

    public Task HandleAsync(CleaningInTransit @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "InTransit", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningStarted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Started", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningInspectionStarted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "InInspection", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningCompleted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Completed", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningInterrupted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Interrupted", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningNeedsHelp @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "WaitingHelp", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningNeedsMaterial @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "WaitingMaterials", NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningCancelled @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Cancelled", NoAdditionalChange, cancellationToken);

    private static void NoAdditionalChange(CleaningScheduleProjectionEntry entry)
    {
    }

    /// <summary>
    /// Idempotent by construction: a redelivered event either finds the row
    /// (harmless no-op re-write of the same status) or, if delivered before
    /// <c>CleaningCreated</c> ever reached this projection (out-of-order
    /// delivery), finds no row and is silently ignored: <c>CleaningCreated</c>
    /// is always the row's sole creator.
    ///
    /// Ordering premise (observed, not assumed): each Cleaning's own lifecycle
    /// events are all published from the SAME producer process
    /// (<c>IHostPro.Api</c>, within a single HTTP-request-triggered command
    /// handler each) through the SAME durable outbox onto the SAME RabbitMQ
    /// queue this projection listens to — a single publisher channel
    /// delivering to a single consumer queue preserves FIFO order per
    /// AggregateId under RabbitMQ's own ordering guarantee (no competing
    /// producer, no multiple queues that could race for the same Cleaning).
    /// The real transport gate tests (<c>CleaningCreatedScheduleProjectionWorkerRoundTripTests</c>/
    /// this checkpoint's own NeedsHelp round trip) exercise a real,
    /// multi-event sequence for the same Cleaning end-to-end and observe no
    /// reordering. No <c>SequenceNumber</c>/aggregate version exists in this
    /// transport today, and none is invented here — if a concrete
    /// out-of-order regression is ever observed, that would need its own
    /// evidence-gated decision, not a speculative fix now.
    /// </summary>
    private Task UpdateAsync(
        Guid tenantId, Guid cleaningId, string status, Action<CleaningScheduleProjectionEntry> applyAdditionalChange,
        CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
            var entry = await _dbContext.CleaningScheduleProjection
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CleaningId == cleaningId, cancellationToken);

            if (entry is not null)
            {
                entry.SetStatus(status);
                applyAdditionalChange(entry);
            }

            return true;
        }, cancellationToken);
}
