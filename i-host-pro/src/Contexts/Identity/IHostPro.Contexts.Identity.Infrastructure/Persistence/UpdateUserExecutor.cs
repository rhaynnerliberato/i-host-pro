using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IUpdateUserExecutor"/>
/// <remarks>
/// Wraps <see cref="IIdentityTransactionExecutor"/> exactly like
/// <c>CreateUserExecutor</c> — no bounded retry, no manual
/// <c>ChangeTracker.Clear()</c>/collector drain: <see cref="IdentityOutboxTransactionExecutor"/>'s
/// own unconditional <c>catch</c> already runs that cleanup before
/// re-throwing, and this executor never re-enters the operation after a
/// failure, so there is nothing left to reset.
///
/// <see cref="DbUpdateConcurrencyException"/> is caught BEFORE the generic
/// <see cref="DbUpdateException"/> clause below — required, not stylistic:
/// it is a subclass, so catch-clause order determines which one a
/// concurrency failure actually matches.
/// </remarks>
public sealed class UpdateUserExecutor : IUpdateUserExecutor
{
    private const string EmailUniqueIndexName = "IX_users_tenant_id_normalized_email";

    private static readonly Error EmailAlreadyInUseError = new(
        IdentityErrorCodes.EmailAlreadyInUse, IdentityErrorCodes.EmailAlreadyInUse);
    private static readonly Error UserConcurrencyConflictError = new(
        IdentityErrorCodes.UserConcurrencyConflict, IdentityErrorCodes.UserConcurrencyConflict);

    private readonly IIdentityTransactionExecutor _transactionExecutor;

    public UpdateUserExecutor(IIdentityTransactionExecutor transactionExecutor) =>
        _transactionExecutor = transactionExecutor;

    public async Task<Result<UserResult>> ExecuteAsync(
        Func<Task<Result<UserResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<UserResult>(UserConcurrencyConflictError);
        }
        catch (DbUpdateException ex) when (IsEmailUniqueViolation(ex))
        {
            return Result.Failure<UserResult>(EmailAlreadyInUseError);
        }
    }

    private static bool IsEmailUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: EmailUniqueIndexName,
        };
}
