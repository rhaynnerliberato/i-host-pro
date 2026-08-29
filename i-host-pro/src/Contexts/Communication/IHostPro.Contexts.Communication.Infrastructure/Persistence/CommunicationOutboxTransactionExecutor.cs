using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence;

/// <inheritdoc cref="ICommunicationTransactionExecutor"/>
/// <remarks>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — replaces the plain
/// <c>CommunicationTransactionExecutor</c> (Fase 9, Checkpoint 1), now that
/// Communication publishes its first Integration Event
/// (<c>ConversationMessageReceived</c>). Mirrors
/// <c>PaymentsOutboxTransactionExecutor</c> exactly: reuses
/// <see cref="TenantAwareTransactionScope"/> for the transaction/RLS
/// mechanics, persists through <see cref="IDbContextOutbox{TDbContext}"/> so
/// any event staged on <see cref="IIntegrationEventCollector"/> is written to
/// this context's durable outbox (<c>communication_messaging</c> schema)
/// atomically with the domain change. Every existing outbound processor
/// (none of which enqueue any event) is functionally unaffected — draining
/// zero events and flushing zero messages is equivalent to a plain
/// <c>SaveChangesAsync</c>.
///
/// <see cref="MessageContext.OverrideStorage"/> is applied from this
/// context's very first checkpoint using this executor — see
/// <c>PaymentsOutboxTransactionExecutor</c>'s own doc comment for the full
/// root-cause explanation: without it, <c>DbContextOutbox&lt;T&gt;</c>
/// inherits <c>Wolverine.Runtime.MessageBus</c>'s constructor, which
/// unconditionally sets <c>Storage = runtime.Storage</c> (the Main store,
/// <c>platform_messaging</c>) regardless of which Ancillary Store this
/// CommunicationDbContext is enrolled to.
/// </remarks>
public sealed class CommunicationOutboxTransactionExecutor : ICommunicationTransactionExecutor
{
    private readonly CommunicationDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<CommunicationDbContext> _outbox;

    public CommunicationOutboxTransactionExecutor(
        CommunicationDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<CommunicationDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine CommunicationDbContext outbox does not support explicit message store selection.");
        }

        var communicationStore = runtime.FindAncillaryStoreForMarkerType(typeof(CommunicationDbContext));
        messageContext.OverrideStorage(communicationStore);
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
