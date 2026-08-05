using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IChangeOwnPasswordExecutor"/>
/// <remarks>
/// No retry loop, unlike <c>BlockUserExecutor</c>/<c>AssignRoleExecutor</c>/
/// <c>RemoveRoleExecutor</c> (Section 8 of the Checkpoint 9 decision) — but
/// still drains <see cref="ISessionRevocationSignal"/> into
/// <see cref="ISessionRevocationCache"/> after a successful commit, exactly
/// like those three. On <see cref="DbUpdateConcurrencyException"/>: clears the
/// <see cref="IdentityDbContext.ChangeTracker"/>, drains the event collector and the
/// revocation signal (both must end up empty either way — Section 8: "collector
/// e signal devem terminar vazios"), then returns the translated failure
/// instead of re-throwing/retrying.
///
/// A second, broader <c>catch</c> below runs the SAME cleanup for every OTHER
/// exception before re-throwing (Checkpoint 9 follow-up review, Section 4:
/// "em qualquer outra exceção antes do commit... nenhuma escrita Redis
/// acontece"). <see cref="IdentityOutboxTransactionExecutor"/>'s own
/// unconditional <c>catch</c> already clears the <c>ChangeTracker</c> and
/// drains the event collector for any exception, making that half of this
/// second clause redundant-but-harmless (clearing an already-cleared tracker,
/// draining an already-empty collector) — it does NOT know about
/// <see cref="ISessionRevocationSignal"/>, which is specific to the
/// session-revocation cascade this executor wraps, so draining it here is the
/// part that is actually necessary: without it, a non-concurrency exception
/// thrown after <c>UserSessionRevoker</c> already staged entries on the signal
/// would leave them behind. Neither <c>catch</c> ever writes to
/// <see cref="ISessionRevocationCache"/> — that only happens in the success
/// path above, so a thrown exception can never reach it regardless.
/// </remarks>
public sealed class ChangeOwnPasswordExecutor : IChangeOwnPasswordExecutor
{
    private static readonly Error UserConcurrencyConflictError = new(
        IdentityErrorCodes.UserConcurrencyConflict, IdentityErrorCodes.UserConcurrencyConflict);

    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IdentityDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;

    public ChangeOwnPasswordExecutor(
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
