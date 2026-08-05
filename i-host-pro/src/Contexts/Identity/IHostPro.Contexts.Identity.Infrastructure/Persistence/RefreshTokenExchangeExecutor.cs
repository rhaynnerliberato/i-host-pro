using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// Runs the Refresh Token exchange's transactional operation with a small,
/// bounded retry specifically for a <see cref="DbUpdateConcurrencyException"/>
/// on the presented token's `xmin` (Incremento 2 plan, Etapa 10 correction).
///
/// This does NOT live in <see cref="IIdentityTransactionExecutor"/> (which was
/// briefly given a generic retry, then reverted): a shared transactional
/// executor used by every auth command cannot safely presume every current or
/// future handler is idempotent-safe to silently re-run — random token/JTI
/// generation, audit writes, caching, or any other external effect a future
/// handler adds could be repeated by a blind retry. This class exists only
/// for <see cref="RefreshTokenCommandHandler"/> specifically, whose full
/// effect set is known and verified here: it stages only in-memory/DB
/// changes and staged Integration Events inside this same transaction
/// (Session touch, RefreshToken rotation, JWT generation, audit entry) —
/// nothing is published, cached, or externally visible until
/// <see cref="IIdentityTransactionExecutor"/>'s single flush-and-commit call
/// (and the surrounding retry here) completes and returns to the real
/// caller. That guarantee is local to this handler and does not generalize —
/// which is exactly why this retry lives here, next to the one use case it
/// is verified safe for, instead of in the shared executor.
///
/// Each retry discards the tracked entities (<see cref="IdentityDbContext.ChangeTracker"/>)
/// before re-running the operation, so the handler's re-executed queries
/// reload the token/session's now-current state from the database — the
/// handler's own existing classification logic (Rotated within/outside the
/// grace window, expired, revoked, etc.) then reclassifies that reloaded
/// state; no concurrency-specific branching exists in the handler itself.
///
/// Also drains <see cref="ISessionRevocationSignal"/> and writes to
/// <see cref="ISessionRevocationCache"/> — but only AFTER the executor call
/// above has returned successfully (transaction committed), never inside the
/// transaction and never for an attempt that gets rolled back (Incremento 2
/// plan, Etapa 12: reuse detected outside the grace window revokes the
/// session, which this cache write accelerates for a future JWT Bearer
/// validator — no external effect inside the transaction).
/// </summary>
/// <inheritdoc cref="IRefreshTokenExchangeExecutor"/>
public sealed class RefreshTokenExchangeExecutor : IRefreshTokenExchangeExecutor
{
    private const int MaxConcurrencyRetryAttempts = 3;

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IdentityDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public RefreshTokenExchangeExecutor(
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

    public async Task<Result<AuthTokensResult>> ExecuteAsync(
        Func<Task<Result<AuthTokensResult>>> operation, CancellationToken cancellationToken)
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
                // IIdentityTransactionExecutor.ExecuteAsync already rolled its
                // own (now-aborted) transaction back — and already cleared the
                // event collector itself — before this exception reached us.
                // Clearing the tracker here is what makes the next attempt's
                // queries reload from the database instead of returning the
                // same now-stale tracked entities. Anything staged in the
                // revocation signal by the failed attempt must never reach the
                // cache either — only the eventually-winning attempt may
                // produce events or a revocation signal.
                _dbContext.ChangeTracker.Clear();
                _eventCollector.Drain();
                _revocationSignal.Drain();
            }
        }
    }
}
