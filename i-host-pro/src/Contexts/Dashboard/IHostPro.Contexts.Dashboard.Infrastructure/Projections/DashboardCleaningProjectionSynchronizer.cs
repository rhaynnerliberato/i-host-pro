using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="DashboardCleaningProjectionEntry"/> in sync with
/// Housekeeping's own ten Cleaning lifecycle events (Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1) — mirrors
/// <c>CleaningScheduleProjectionSynchronizer</c>'s own status-string mapping
/// exactly (each event → the real <c>CleaningStatus</c> code it represents,
/// per Documento 07 §29.7's transition→event→status matrix). <c>CleaningDelayed</c>
/// remains deliberately NOT consumed — same reasoning as
/// <c>CleaningScheduleProjectionSynchronizer</c>'s own doc comment: it
/// changes no field this projection displays.
///
/// Out-of-order delivery guard — see
/// <c>DashboardReservationProjectionSynchronizer</c>'s own doc comment.
/// </summary>
public sealed class DashboardCleaningProjectionSynchronizer :
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
    private readonly DashboardDbContext _dbContext;
    private readonly IDashboardTransactionExecutor _executor;

    public DashboardCleaningProjectionSynchronizer(DashboardDbContext dbContext, IDashboardTransactionExecutor executor)
    {
        _dbContext = dbContext;
        _executor = executor;
    }

    /// <summary>Idempotent by construction: a redelivered <c>CleaningCreated</c> finds the row already present (harmless no-op).</summary>
    public Task HandleAsync(CleaningCreated @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var exists = await _dbContext.CleaningProjection
                .AnyAsync(c => c.TenantId == @event.TenantId && c.CleaningId == @event.CleaningId, cancellationToken);

            if (!exists)
            {
                _dbContext.CleaningProjection.Add(new DashboardCleaningProjectionEntry(
                    @event.TenantId, @event.CleaningId, @event.PropertyId, @event.Status,
                    @event.ScheduledAtUtc, @event.Timestamp));
            }

            return true;
        }, cancellationToken);

    public Task HandleAsync(CleaningAssigned @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Assigned", @event.Timestamp,
            entry => entry.SetAssignedHousekeeper(@event.HousekeeperUserId), cancellationToken);

    public Task HandleAsync(CleaningInTransit @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "InTransit", @event.Timestamp, NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningStarted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Started", @event.Timestamp,
            entry => entry.SetStarted(@event.Timestamp), cancellationToken);

    public Task HandleAsync(CleaningInspectionStarted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "InInspection", @event.Timestamp, NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningCompleted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Completed", @event.Timestamp,
            entry => entry.SetCompleted(@event.Timestamp), cancellationToken);

    public Task HandleAsync(CleaningInterrupted @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Interrupted", @event.Timestamp, NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningNeedsHelp @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "WaitingHelp", @event.Timestamp, NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningNeedsMaterial @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "WaitingMaterials", @event.Timestamp, NoAdditionalChange, cancellationToken);

    public Task HandleAsync(CleaningCancelled @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.CleaningId, "Cancelled", @event.Timestamp, NoAdditionalChange, cancellationToken);

    private static void NoAdditionalChange(DashboardCleaningProjectionEntry entry)
    {
    }

    /// <summary>
    /// Idempotent by construction: a redelivered event either finds the row
    /// (harmless no-op re-write of the same status) or, if delivered before
    /// <c>CleaningCreated</c> ever reached this projection, finds no row and
    /// is silently ignored — <c>CleaningCreated</c> is always the row's sole
    /// creator. Out-of-order guard: applies only when
    /// <c>@event.Timestamp &gt;= entry.LastEventAtUtc</c>.
    /// </summary>
    private Task UpdateAsync(
        Guid tenantId, Guid cleaningId, string status, DateTimeOffset eventAtUtc,
        Action<DashboardCleaningProjectionEntry> applyAdditionalChange, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
            var entry = await _dbContext.CleaningProjection
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CleaningId == cleaningId, cancellationToken);

            if (entry is not null && eventAtUtc >= entry.LastEventAtUtc)
            {
                entry.SetStatus(status, eventAtUtc);
                applyAdditionalChange(entry);
            }

            return true;
        }, cancellationToken);
}
