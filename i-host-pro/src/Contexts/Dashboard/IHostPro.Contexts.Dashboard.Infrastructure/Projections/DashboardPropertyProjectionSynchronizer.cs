using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="DashboardPropertyProjectionEntry"/> in sync with
/// Property Management's own <c>PropertyCreated</c>/<c>PropertyActivated</c>/
/// <c>PropertyDeactivated</c>/<c>PropertyArchived</c> (Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1) — mirrors
/// <c>PropertyProjectionSynchronizer</c>'s (Housekeeping) own separation.
/// <c>PropertyUpdated</c> is deliberately NOT consumed (Checkpoint 0
/// decision, §33 — it never changes status). Status codes mirror
/// <c>PropertyManagement.Application.Properties.PropertyStatusCodeMapper</c>'s
/// own stable lowercase codes exactly — <c>PropertyCreated</c> is the only
/// one of the four that carries <c>Status</c> in its own payload (always
/// "draft"); the other three carry only <c>PropertyId</c>, so the status is
/// the event's own name translated to the matching code.
///
/// Out-of-order delivery guard — see
/// <c>DashboardReservationProjectionSynchronizer</c>'s own doc comment.
/// </summary>
public sealed class DashboardPropertyProjectionSynchronizer :
    IIntegrationEventHandler<PropertyCreated>,
    IIntegrationEventHandler<PropertyActivated>,
    IIntegrationEventHandler<PropertyDeactivated>,
    IIntegrationEventHandler<PropertyArchived>
{
    private readonly DashboardDbContext _dbContext;
    private readonly IDashboardTransactionExecutor _executor;

    public DashboardPropertyProjectionSynchronizer(DashboardDbContext dbContext, IDashboardTransactionExecutor executor)
    {
        _dbContext = dbContext;
        _executor = executor;
    }

    /// <summary>Idempotent by construction: a redelivered <c>PropertyCreated</c> finds the row already present (harmless no-op).</summary>
    public Task HandleAsync(PropertyCreated @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var exists = await _dbContext.PropertyProjection
                .AnyAsync(p => p.TenantId == @event.TenantId && p.PropertyId == @event.PropertyId, cancellationToken);

            if (!exists)
            {
                _dbContext.PropertyProjection.Add(new DashboardPropertyProjectionEntry(
                    @event.TenantId, @event.PropertyId, @event.Status, @event.Timestamp));
            }

            return true;
        }, cancellationToken);

    public Task HandleAsync(PropertyActivated @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.PropertyId, "active", @event.Timestamp, cancellationToken);

    public Task HandleAsync(PropertyDeactivated @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.PropertyId, "inactive", @event.Timestamp, cancellationToken);

    public Task HandleAsync(PropertyArchived @event, CancellationToken cancellationToken = default) =>
        UpdateAsync(@event.TenantId, @event.PropertyId, "archived", @event.Timestamp, cancellationToken);

    /// <summary>Idempotent, out-of-order guarded — see <c>DashboardCleaningProjectionSynchronizer.UpdateAsync</c>'s own doc comment.</summary>
    private Task UpdateAsync(Guid tenantId, Guid propertyId, string status, DateTimeOffset eventAtUtc, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
            var entry = await _dbContext.PropertyProjection
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId, cancellationToken);

            if (entry is not null && eventAtUtc >= entry.LastEventAtUtc)
                entry.SetStatus(status, eventAtUtc);

            return true;
        }, cancellationToken);
}
