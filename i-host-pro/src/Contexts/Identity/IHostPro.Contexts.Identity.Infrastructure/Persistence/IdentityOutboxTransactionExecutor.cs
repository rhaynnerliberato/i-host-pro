using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IIdentityTransactionExecutor"/>
/// <remarks>
/// Reuses <see cref="TenantAwareTransactionScope"/> — the exact transaction/RLS
/// mechanics <c>TenantAwareUnitOfWork</c> uses — but persists through
/// <see cref="IDbContextOutbox{TDbContext}"/> instead of a plain
/// <c>SaveChangesAsync</c>, so any event staged on
/// <see cref="IIntegrationEventCollector"/> by the handler is written to
/// Identity's durable outbox (<c>identity_messaging</c> schema) atomically
/// with the domain change, in the same PostgreSQL transaction (Incremento 2
/// plan, Etapa 15A).
///
/// <see cref="IIntegrationEventCollector.Drain"/> is called (and its result
/// discarded) both before the operation runs and on any exception, so a
/// retried or aborted attempt never contaminates the next attempt and never
/// leaves an event queued for a transaction that will not be committed —
/// mirroring exactly how <c>RefreshTokenExchangeExecutor</c>/<c>LogoutExecutor</c>
/// already discard <c>ISessionRevocationSignal</c> on a concurrency retry. A
/// <c>Result.Failure</c> returned by <paramref name="operation"/> is not
/// special-cased: it commits like any other outcome, so a rejected login can
/// still persist a lockout increment, an audit entry and a <c>LoginFailed</c>/
/// <c>AccountLockedOut</c> event. Only an exception causes a rollback.
///
/// Unlike <see cref="TenantAwareUnitOfWork"/>, this class never calls
/// <c>transaction.CommitAsync()</c> itself — confirmed empirically (Incremento
/// 2 plan, Etapa 15A): <see cref="IDbContextOutbox{TDbContext}.SaveChangesAndFlushMessagesAsync"/>
/// commits the ambient transaction (the one <see cref="TenantAwareTransactionScope.BeginAsync"/>
/// opened, and that <c>SET LOCAL app.tenant_id</c> already ran on) as part of
/// its own transactional-outbox protocol; calling <c>CommitAsync()</c>
/// afterward throws (`NpgsqlTransaction has completed`). On any exception
/// before that point, the transaction was never committed, so <c>await
/// using</c> disposing it below still rolls it back correctly.
/// </remarks>
public sealed class IdentityOutboxTransactionExecutor : IIdentityTransactionExecutor
{
    private readonly IdentityDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<IdentityDbContext> _outbox;

    public IdentityOutboxTransactionExecutor(
        IdentityDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<IdentityDbContext> outbox)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;
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
