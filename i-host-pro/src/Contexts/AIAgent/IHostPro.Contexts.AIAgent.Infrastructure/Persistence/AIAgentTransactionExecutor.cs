using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

/// <inheritdoc cref="IAIAgentTransactionExecutor"/>
/// <remarks>
/// Mirrors <c>CommunicationOutboxTransactionExecutor</c>/<c>PaymentsOutboxTransactionExecutor</c>'s
/// own structure, minus an <c>IIntegrationEventCollector</c> — AI Agent
/// publishes no Integration Event of its own this checkpoint (mandate item
/// 29: "não publicar eventos artificiais apenas para justificar outbox").
/// Still resolves through <see cref="IDbContextOutbox{TDbContext}"/> (never a
/// plain <c>SaveChangesAsync</c>) because this is the same
/// empirically-confirmed requirement every other write-capable Bounded
/// Context needs for <c>IDbContextOutbox&lt;TDbContext&gt;</c> to resolve
/// inside a Wolverine-hosted handler at all — see
/// <c>ReservationsOutboxTransactionExecutor</c>'s own precedent (Fase 7).
///
/// <see cref="MessageContext.OverrideStorage"/> is applied from this
/// context's very first checkpoint using this executor — see
/// <c>PaymentsOutboxTransactionExecutor</c>'s own doc comment for the full
/// root-cause explanation: without it, <c>DbContextOutbox&lt;T&gt;</c>
/// inherits <c>Wolverine.Runtime.MessageBus</c>'s constructor, which
/// unconditionally sets <c>Storage = runtime.Storage</c> (the Main store)
/// regardless of which Ancillary Store this AIAgentDbContext is enrolled to.
/// </remarks>
public sealed class AIAgentTransactionExecutor : IAIAgentTransactionExecutor
{
    private readonly AIAgentDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IDbContextOutbox<AIAgentDbContext> _outbox;

    public AIAgentTransactionExecutor(
        AIAgentDbContext dbContext,
        ITenantContext tenantContext,
        IDbContextOutbox<AIAgentDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine AIAgentDbContext outbox does not support explicit message store selection.");
        }

        var aiAgentStore = runtime.FindAncillaryStoreForMarkerType(typeof(AIAgentDbContext));
        messageContext.OverrideStorage(aiAgentStore);
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken)
    {
        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, _tenantContext, readOnly: false, cancellationToken);

        try
        {
            var result = await operation();

            await _outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

            return result;
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }
}
