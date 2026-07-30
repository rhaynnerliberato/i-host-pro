using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IAssignRoleExecutor"/>
/// <remarks>
/// Structurally close to <see cref="RevokeOwnSessionExecutor"/> — see its doc
/// comment for the general rationale on the bounded concurrency retry
/// (Incremento 3, Checkpoint 6: this command's session-revocation cascade can
/// mutate any of the target user's active sessions/refresh tokens, any one of
/// which could race against a concurrent Refresh on that same session) — with
/// one deliberate correction (Checkpoint 6 retry-safety review): cleanup
/// (<c>ChangeTracker.Clear()</c>/draining the collector/draining the
/// revocation signal) now runs on EVERY <see cref="DbUpdateConcurrencyException"/>,
/// including the final, non-retried one, never only on retry-eligible
/// attempts. The previous <c>when (attempt &lt; MaxConcurrencyRetryAttempts)</c>
/// exception filter skipped this cleanup on the last attempt — harmless in
/// practice today (the scoped <see cref="ISessionRevocationSignal"/> instance
/// is discarded along with the failed request either way), but it violated
/// the invariant that the signal must always end up empty after
/// <see cref="ExecuteAsync"/> returns or throws, retried or not. Confirmed
/// empirically the same latent gap exists, unchanged, in
/// <see cref="RevokeOwnSessionExecutor"/>/<c>LogoutExecutor</c> — out of scope
/// to touch here (Checkpoint 6 review explicitly limited this correction to
/// AssignRole/RemoveRole's own executors).
///
/// Also drains <see cref="ISessionRevocationSignal"/> and writes to
/// <see cref="ISessionRevocationCache"/> — but only AFTER the executor call
/// below has returned successfully (transaction committed), never inside the
/// transaction and never for an attempt that gets rolled back.
/// </remarks>
public sealed class AssignRoleExecutor : IAssignRoleExecutor
{
    private const int MaxConcurrencyRetryAttempts = 3;

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly DbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public AssignRoleExecutor(
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
            catch (DbUpdateConcurrencyException)
            {
                // The failed attempt's transaction was already rolled back by
                // IIdentityTransactionExecutor, which also already cleared the
                // event collector itself; discard anything staged here too —
                // it must never reach the cache. Only the eventually-winning
                // attempt may produce events or a revocation signal. Runs
                // unconditionally — including on the final, exhausted attempt
                // below — so the signal is guaranteed empty whether this
                // method eventually returns or throws.
                _dbContext.ChangeTracker.Clear();
                _eventCollector.Drain();
                _revocationSignal.Drain();

                if (attempt >= MaxConcurrencyRetryAttempts)
                    throw;
            }
        }
    }
}
