using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="IHousekeepingTransactionExecutor"/>
/// <remarks>
/// Mirrors <c>ReservationsOutboxTransactionExecutor</c> exactly: reuses
/// <see cref="TenantAwareTransactionScope"/> for the transaction/RLS
/// mechanics, persists through <see cref="IDbContextOutbox{TDbContext}"/> so
/// any event staged on <see cref="IIntegrationEventCollector"/> is written to
/// this context's durable outbox (<c>housekeeping_messaging</c> schema)
/// atomically with the domain change.
///
/// <see cref="MessageContext.OverrideStorage"/> is applied from this
/// context's very first checkpoint — see
/// <c>ReservationsOutboxTransactionExecutor</c>'s own doc comment for the
/// full root-cause explanation of why this is required (Fase 2, Checkpoint
/// 6 homologação): without it, <c>DbContextOutbox&lt;T&gt;</c> inherits
/// <c>Wolverine.Runtime.MessageBus</c>'s constructor, which unconditionally
/// sets <c>Storage = runtime.Storage</c> (the Main store,
/// <c>platform_messaging</c>) regardless of which Ancillary Store this
/// HousekeepingDbContext is enrolled to.
/// </remarks>
public sealed class HousekeepingOutboxTransactionExecutor : IHousekeepingTransactionExecutor
{
    private readonly HousekeepingDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<HousekeepingDbContext> _outbox;

    public HousekeepingOutboxTransactionExecutor(
        HousekeepingDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<HousekeepingDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine HousekeepingDbContext outbox does not support explicit message store selection.");
        }

        var housekeepingStore = runtime.FindAncillaryStoreForMarkerType(typeof(HousekeepingDbContext));
        messageContext.OverrideStorage(housekeepingStore);
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
