using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IPropertyManagementTransactionExecutor"/>
/// <remarks>
/// Mirrors <c>IdentityOutboxTransactionExecutor</c> exactly: reuses
/// <see cref="TenantAwareTransactionScope"/> for the transaction/RLS
/// mechanics, but persists through <see cref="IDbContextOutbox{TDbContext}"/>
/// instead of a plain <c>SaveChangesAsync</c>, so any event staged on
/// <see cref="IIntegrationEventCollector"/> is written to this context's
/// durable outbox (<c>property_management_messaging</c> schema) atomically
/// with the domain change, in the same PostgreSQL transaction (Checkpoint 2
/// plan, item 11/12).
///
/// <see cref="MessageContext.OverrideStorage"/> — see
/// <c>IdentityOutboxTransactionExecutor</c>'s own doc comment for the full
/// root-cause explanation (identical mechanism, mirrored here for
/// <c>property_management_messaging</c>).
/// </remarks>
public sealed class PropertyManagementOutboxTransactionExecutor : IPropertyManagementTransactionExecutor
{
    private readonly PropertyManagementDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<PropertyManagementDbContext> _outbox;

    public PropertyManagementOutboxTransactionExecutor(
        PropertyManagementDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<PropertyManagementDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine DbContext outbox does not support explicit message store selection.");
        }

        var propertyManagementStore = runtime.FindAncillaryStoreForMarkerType(typeof(PropertyManagementDbContext));
        messageContext.OverrideStorage(propertyManagementStore);
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
