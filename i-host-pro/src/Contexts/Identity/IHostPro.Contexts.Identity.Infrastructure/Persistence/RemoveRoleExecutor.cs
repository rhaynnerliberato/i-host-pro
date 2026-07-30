using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IRemoveRoleExecutor"/>
/// <remarks>
/// Structurally identical to <see cref="AssignRoleExecutor"/> — see its doc
/// comment for the full rationale, including the Checkpoint 6 retry-safety
/// correction (cleanup runs on every <see cref="DbUpdateConcurrencyException"/>,
/// including the final, exhausted attempt).
/// </remarks>
public sealed class RemoveRoleExecutor : IRemoveRoleExecutor
{
    private const int MaxConcurrencyRetryAttempts = 3;

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly DbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public RemoveRoleExecutor(
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
                _dbContext.ChangeTracker.Clear();
                _eventCollector.Drain();
                _revocationSignal.Drain();

                if (attempt >= MaxConcurrencyRetryAttempts)
                    throw;
            }
        }
    }
}
