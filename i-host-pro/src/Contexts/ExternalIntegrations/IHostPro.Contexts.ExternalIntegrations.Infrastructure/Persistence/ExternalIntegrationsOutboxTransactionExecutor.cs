using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <inheritdoc cref="IExternalIntegrationsTransactionExecutor"/>
/// <remarks>
/// Mirrors <c>Reservations.Infrastructure.Persistence.ReservationsOutboxTransactionExecutor</c>
/// exactly, including its two confirmed-empirically requirements: the
/// constructor's <see cref="MessageContext.OverrideStorage"/> call (without
/// it, <see cref="IDbContextOutbox{T}"/> silently writes to the Main
/// <c>platform_messaging</c> store instead of this context's own ancillary
/// <c>external_integrations_messaging</c> store), and the host's own
/// <c>opts.UseEntityFrameworkCoreTransactions()</c> call (without it,
/// <see cref="IDbContextOutbox{ExternalIntegrationsDbContext}"/> never gets
/// registered by Wolverine's DI wiring at all).
/// </remarks>
public sealed class ExternalIntegrationsOutboxTransactionExecutor : IExternalIntegrationsTransactionExecutor
{
    private readonly ExternalIntegrationsDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<ExternalIntegrationsDbContext> _outbox;

    public ExternalIntegrationsOutboxTransactionExecutor(
        ExternalIntegrationsDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<ExternalIntegrationsDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine ExternalIntegrationsDbContext outbox does not support explicit message store selection.");
        }

        var externalIntegrationsStore = runtime.FindAncillaryStoreForMarkerType(typeof(ExternalIntegrationsDbContext));
        messageContext.OverrideStorage(externalIntegrationsStore);
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
