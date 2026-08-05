using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Sessions;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IRevokeOwnSessionExecutor"/>
/// <remarks>
/// Structurally identical to <see cref="LogoutExecutor"/> — see its doc
/// comment for the full rationale on the bounded concurrency retry
/// (Incremento 3, Checkpoint 4: this command mutates the exact same rows —
/// the target session and its active refresh tokens — so the same race
/// against a concurrent Refresh applies here).
///
/// Also drains <see cref="ISessionRevocationSignal"/> and writes to
/// <see cref="ISessionRevocationCache"/> — but only AFTER the executor call
/// below has returned successfully (transaction committed), never inside the
/// transaction and never for an attempt that gets rolled back (Incremento 2
/// plan, Etapa 12: no external effect inside the transaction). On the
/// "session not owned" rejection path, <see cref="RevokeOwnSessionCommandHandler"/>
/// never calls <c>ISessionRevocationSignal.MarkRevoked</c> in the first
/// place, so <c>Drain()</c> here naturally yields nothing to write for that
/// path either.
/// </remarks>
public sealed class RevokeOwnSessionExecutor : IRevokeOwnSessionExecutor
{
    private const int MaxConcurrencyRetryAttempts = 3;

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IdentityDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public RevokeOwnSessionExecutor(
        IIdentityTransactionExecutor transactionExecutor,
        IdentityDbContext dbContext,
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
