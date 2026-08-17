using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="DashboardReservationProjectionEntry"/> in sync with
/// Reservations' own <c>ReservationCreated</c>/<c>ReservationUpdated</c>/
/// <c>ReservationCancelled</c> (Fase 7, Incremento 2 — Dashboard &amp;
/// Reporting Foundation, Checkpoint 1) — mirrors
/// <c>CleaningScheduleProjectionSynchronizer</c>'s own separation exactly.
/// This class name deliberately does NOT end in "Handler" — same
/// double-discovery avoidance already established for every other
/// synchronizer in this platform.
///
/// Out-of-order delivery guard (Checkpoint 0 decision, §29): an event only
/// applies if its own <c>Timestamp</c> is not earlier than the row's current
/// <c>LastEventAtUtc</c> — proven safe by
/// <c>DashboardReservationProjectionSynchronizerTests</c> (an artificially
/// out-of-order-delivered older event never regresses a newer state).
/// </summary>
public sealed class DashboardReservationProjectionSynchronizer :
    IIntegrationEventHandler<ReservationCreated>,
    IIntegrationEventHandler<ReservationUpdated>,
    IIntegrationEventHandler<ReservationCancelled>
{
    private readonly DashboardDbContext _dbContext;
    private readonly IDashboardTransactionExecutor _executor;

    public DashboardReservationProjectionSynchronizer(DashboardDbContext dbContext, IDashboardTransactionExecutor executor)
    {
        _dbContext = dbContext;
        _executor = executor;
    }

    /// <summary>Idempotent by construction: a redelivered <c>ReservationCreated</c> finds the row already present (harmless no-op).</summary>
    public Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var exists = await _dbContext.ReservationProjection
                .AnyAsync(r => r.TenantId == @event.TenantId && r.ReservationId == @event.ReservationId, cancellationToken);

            if (!exists)
            {
                _dbContext.ReservationProjection.Add(new DashboardReservationProjectionEntry(
                    @event.TenantId, @event.ReservationId, @event.PropertyId, @event.Status,
                    @event.CheckInAt, @event.CheckOutAt, @event.Timestamp));
            }

            return true;
        }, cancellationToken);

    public Task HandleAsync(ReservationUpdated @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var entry = await _dbContext.ReservationProjection
                .FirstOrDefaultAsync(r => r.TenantId == @event.TenantId && r.ReservationId == @event.ReservationId, cancellationToken);

            if (entry is not null && @event.Timestamp >= entry.LastEventAtUtc)
                entry.UpdateSchedule(@event.CheckInAt, @event.CheckOutAt, @event.Timestamp);

            return true;
        }, cancellationToken);

    public Task HandleAsync(ReservationCancelled @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var entry = await _dbContext.ReservationProjection
                .FirstOrDefaultAsync(r => r.TenantId == @event.TenantId && r.ReservationId == @event.ReservationId, cancellationToken);

            if (entry is not null && @event.Timestamp >= entry.LastEventAtUtc)
                entry.Cancel(@event.Timestamp);

            return true;
        }, cancellationToken);
}
