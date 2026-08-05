using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IAdminResetPasswordExecutor"/>
/// <remarks>
/// Structurally identical to <see cref="ChangeOwnPasswordExecutor"/> — see its
/// doc comment for the full no-retry-plus-post-commit-cache-write rationale,
/// including the second, broader <c>catch</c> that drains
/// <see cref="ISessionRevocationSignal"/> for any non-concurrency exception
/// too (Checkpoint 9 follow-up review, Section 4).
/// </remarks>
public sealed class AdminResetPasswordExecutor : IAdminResetPasswordExecutor
{
    private static readonly Error UserConcurrencyConflictError = new(
        IdentityErrorCodes.UserConcurrencyConflict, IdentityErrorCodes.UserConcurrencyConflict);

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IdentityDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public AdminResetPasswordExecutor(
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

            return Result.Failure(UserConcurrencyConflictError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();
            _revocationSignal.Drain();

            throw;
        }
    }
}
