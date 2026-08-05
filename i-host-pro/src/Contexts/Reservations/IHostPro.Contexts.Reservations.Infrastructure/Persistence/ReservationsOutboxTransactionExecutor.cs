using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IReservationsTransactionExecutor"/>
/// <remarks>
/// Mirrors <c>PropertyManagementOutboxTransactionExecutor</c>/
/// <c>IdentityOutboxTransactionExecutor</c> exactly: reuses
/// <see cref="TenantAwareTransactionScope"/> for the transaction/RLS
/// mechanics, persists through <see cref="IDbContextOutbox{TDbContext}"/> so
/// any event staged on <see cref="IIntegrationEventCollector"/> is written to
/// this context's durable outbox (<c>reservations_messaging</c> schema)
/// atomically with the domain change.
///
/// <see cref="MessageContext.OverrideStorage"/> is applied from this
/// context's very first checkpoint (Fase 3, Incremento 1) — see
/// <c>PropertyManagementOutboxTransactionExecutor</c>'s own doc comment for
/// the full root-cause explanation of why this is required (Fase 2,
/// Checkpoint 6 homologação): without it, <c>DbContextOutbox&lt;T&gt;</c>
/// inherits <c>Wolverine.Runtime.MessageBus</c>'s constructor, which
/// unconditionally sets <c>Storage = runtime.Storage</c> (the Main store,
/// <c>platform_messaging</c>) regardless of which Ancillary Store this
/// ReservationsDbContext is enrolled to — every outgoing envelope's persistence AND its
/// post-publish DELETE would silently target the wrong store. Applying the
/// fix here from day one means this Bounded Context never reproduces either
/// of the two now-corrected defects.
/// </remarks>
public sealed class ReservationsOutboxTransactionExecutor : IReservationsTransactionExecutor
{
    private readonly ReservationsDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<ReservationsDbContext> _outbox;

    public ReservationsOutboxTransactionExecutor(
        ReservationsDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<ReservationsDbContext> outbox,
        IWolverineRuntime runtime)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine ReservationsDbContext outbox does not support explicit message store selection.");
        }

        var reservationsStore = runtime.FindAncillaryStoreForMarkerType(typeof(ReservationsDbContext));
        messageContext.OverrideStorage(reservationsStore);
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
