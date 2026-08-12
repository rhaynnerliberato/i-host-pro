using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="PropertyProjectionEntry"/> in sync with Property
/// Management's own lifecycle events (Fase 6, Incremento 1, Checkpoint 0/3
/// gate) — the business logic behind four separate, minimal Wolverine
/// adapters (<c>PropertyCreatedHandler</c>/<c>PropertyActivatedHandler</c>/
/// <c>PropertyDeactivatedHandler</c>/<c>PropertyArchivedHandler</c>), never
/// referencing Wolverine itself — mirrors
/// <c>PolicyUpdatedCacheInvalidation</c>'s own separation exactly. This
/// class name deliberately does NOT end in "Handler" — Wolverine's own
/// naming-convention discovery (class name ending in <c>Handler</c>, public
/// method named <c>Handle</c>/<c>Consume</c>) would otherwise match this
/// class too, exactly the double-discovery defect Fase 5's Checkpoint 7
/// (§13.11) found and fixed for <c>PolicyUpdatedCacheInvalidation</c>.
///
/// Uses <see cref="IHousekeepingTransactionExecutor"/> for the
/// tenant-aware/RLS-protected write even though no Integration Event is
/// published here — the executor's outbox-drain/publish step is a harmless
/// no-op when nothing was staged.
/// </summary>
public sealed class PropertyProjectionSynchronizer :
    IIntegrationEventHandler<PropertyCreated>,
    IIntegrationEventHandler<PropertyActivated>,
    IIntegrationEventHandler<PropertyDeactivated>,
    IIntegrationEventHandler<PropertyArchived>
{
    private readonly HousekeepingDbContext _dbContext;
    private readonly IHousekeepingTransactionExecutor _executor;

    public PropertyProjectionSynchronizer(HousekeepingDbContext dbContext, IHousekeepingTransactionExecutor executor)
    {
        _dbContext = dbContext;
        _executor = executor;
    }

    /// <summary>
    /// <c>PropertyCreated</c> is always born <c>draft</c> (never <c>Active</c>
    /// at creation — Property Management's own contract) — the projection
    /// row is created with <c>IsActive = false</c>.
    /// </summary>
    public Task HandleAsync(PropertyCreated @event, CancellationToken cancellationToken) =>
        UpsertAsync(@event.TenantId, @event.PropertyId, isActive: false, cancellationToken);

    public Task HandleAsync(PropertyActivated @event, CancellationToken cancellationToken) =>
        UpsertAsync(@event.TenantId, @event.PropertyId, isActive: true, cancellationToken);

    public Task HandleAsync(PropertyDeactivated @event, CancellationToken cancellationToken) =>
        UpsertAsync(@event.TenantId, @event.PropertyId, isActive: false, cancellationToken);

    public Task HandleAsync(PropertyArchived @event, CancellationToken cancellationToken) =>
        UpsertAsync(@event.TenantId, @event.PropertyId, isActive: false, cancellationToken);

    /// <summary>
    /// Idempotent by construction: a redelivered event either finds no
    /// existing row (creates it) or an existing row already at the target
    /// <see cref="PropertyProjectionEntry.IsActive"/> value (a harmless
    /// no-op write).
    /// </summary>
    private Task UpsertAsync(Guid tenantId, Guid propertyId, bool isActive, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(async () =>
        {
            var entry = await _dbContext.PropertyProjection
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId, cancellationToken);

            if (entry is null)
                _dbContext.PropertyProjection.Add(new PropertyProjectionEntry(tenantId, propertyId, isActive));
            else
                entry.SetActive(isActive);

            return true;
        }, cancellationToken);
}
