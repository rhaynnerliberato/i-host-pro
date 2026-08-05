using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IBlockUserExecutor"/>
/// <remarks>
/// Structurally identical to <see cref="AssignRoleExecutor"/>/<see cref="RemoveRoleExecutor"/>
/// (Incremento 3, Checkpoint 7) — built with the Checkpoint 6 retry-safety
/// correction already in place from the start: cleanup
/// (<c>ChangeTracker.Clear()</c>/draining the event collector/draining the
/// revocation signal) runs on every <see cref="DbUpdateConcurrencyException"/>,
/// including the final, non-retried one, before conditionally re-throwing.
/// See <see cref="AssignRoleExecutor"/>'s doc comment for the full retry
/// rationale and for the note that <see cref="RevokeOwnSessionExecutor"/>/
/// <c>LogoutExecutor</c> still carry the pre-Checkpoint-6 pattern
/// (acknowledged technical debt, explicitly out of scope to touch here).
/// </remarks>
public sealed class BlockUserExecutor : IBlockUserExecutor
{
    private const int MaxConcurrencyRetryAttempts = 3;

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IdentityDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public BlockUserExecutor(
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
            catch (DbUpdateConcurrencyException)
            {
                // Runs unconditionally — including on the final, exhausted
                // attempt below — so ChangeTracker/collector/signal are all
                // guaranteed to end up clean whether this method eventually
                // returns or throws (Checkpoint 6 retry-safety review).
                _dbContext.ChangeTracker.Clear();
                _eventCollector.Drain();
                _revocationSignal.Drain();

                if (attempt >= MaxConcurrencyRetryAttempts)
                    throw;
            }
        }
    }
}
