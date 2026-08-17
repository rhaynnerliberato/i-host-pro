using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence;

/// <inheritdoc cref="IDashboardTransactionExecutor"/>
/// <remarks>
/// Mirrors <c>HousekeepingOutboxTransactionExecutor</c>/
/// <c>ReservationsOutboxTransactionExecutor</c> exactly: reuses
/// <see cref="TenantAwareTransactionScope"/> for the transaction/RLS
/// mechanics, persists through <see cref="IDbContextOutbox{TDbContext}"/> so
/// <see cref="DashboardDbContext"/>'s own SaveChanges commits atomically
/// inside a Wolverine-managed transaction. <see cref="IIntegrationEventCollector"/>
/// is drained on every call exactly like the other two contexts', even
/// though no Dashboard synchronizer ever calls <c>Enqueue</c> this increment
/// (Checkpoint 0 decision, §13) — an always-empty drain is a harmless no-op,
/// and keeping the same shape avoids a Dashboard-specific executor variant.
/// </remarks>
public sealed class DashboardOutboxTransactionExecutor : IDashboardTransactionExecutor
{
    private readonly DashboardDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<DashboardDbContext> _outbox;

    public DashboardOutboxTransactionExecutor(
        DashboardDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<DashboardDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine DashboardDbContext outbox does not support explicit message store selection.");
        }

        var dashboardStore = runtime.FindAncillaryStoreForMarkerType(typeof(DashboardDbContext));
        messageContext.OverrideStorage(dashboardStore);
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken)
    {
        _collector.Drain();

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, _tenantContext, readOnly: false, cancellationToken);

        try
        {
            var result = await operation();

            foreach (var @event in _collector.Drain())
                await _outbox.PublishAsync(@event);

            await _outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

            return result;
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _collector.Drain();
            throw;
        }
    }
}
