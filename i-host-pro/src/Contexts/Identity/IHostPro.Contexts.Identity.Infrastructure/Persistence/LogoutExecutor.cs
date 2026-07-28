using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="ILogoutExecutor"/>
/// <remarks>
/// Structurally identical to <see cref="RefreshTokenExchangeExecutor"/> — see
/// its doc comment for the full rationale on why this bounded retry lives
/// here, local to Logout, rather than in the shared
/// <see cref="IIdentityTransactionExecutor"/> (Incremento 2 plan, Etapa 11
/// correction: a Logout racing a concurrent Refresh for the same refresh
/// token row can hit a genuine <see cref="DbUpdateConcurrencyException"/> —
/// LogoutCommandHandler performs no external, non-idempotent effect before
/// the single flush-and-commit call this wraps, so re-running it from
/// scratch is safe here specifically).
///
/// Also drains <see cref="ISessionRevocationSignal"/> and writes to
/// <see cref="ISessionRevocationCache"/> — but only AFTER the executor call
/// above has returned successfully (transaction committed), never inside the
/// transaction and never for an attempt that gets rolled back (Incremento 2
/// plan, Etapa 12: no external effect inside the transaction).
/// </remarks>
public sealed class LogoutExecutor : ILogoutExecutor
{
    private const int MaxConcurrencyRetryAttempts = 3;

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly DbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public LogoutExecutor(
        IIdentityTransactionExecutor transactionExecutor,
        DbContext dbContext,
        IIntegrationEventCollector eventCollector,
        ISessionRevocationSignal revocationSignal,
        ISessionRevocationCache revocationCache)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
        _revocationSignal = revocationSignal;
        _revocationCache = revocationCache;
    }

    public async Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await _transactionExecutor.ExecuteAsync(operation, cancellationToken);

                foreach (var (tenantId, sessionId) in _revocationSignal.Drain())
                    await _revocationCache.MarkRevokedAsync(tenantId, sessionId, cancellationToken);

                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetryAttempts)
            {
                // The failed attempt's transaction was already rolled back by
                // IIdentityTransactionExecutor, which also already cleared the
                // event collector itself; discard anything staged here too —
                // it must never reach the cache. Only the eventually-winning
                // attempt may produce events or a revocation signal.
                _dbContext.ChangeTracker.Clear();
                _eventCollector.Drain();
                _revocationSignal.Drain();
            }
        }
    }
}
