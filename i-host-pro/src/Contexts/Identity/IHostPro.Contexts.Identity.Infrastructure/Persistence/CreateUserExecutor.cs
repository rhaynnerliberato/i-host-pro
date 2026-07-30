using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="ICreateUserExecutor"/>
/// <remarks>
/// Wraps <see cref="IIdentityTransactionExecutor"/> exactly like
/// <c>LogoutExecutor</c>/<c>RevokeOwnSessionExecutor</c> do, but with a
/// different post-processing step: instead of a post-commit cache write,
/// this catches the specific PostgreSQL unique-violation
/// (<c>23505</c>) on the <c>IX_users_tenant_id_normalized_email</c> index —
/// the exact name EF Core's migration gives it — and translates it into
/// <see cref="IdentityErrorCodes.EmailAlreadyInUse"/>. Any OTHER
/// <see cref="DbUpdateException"/> (a different constraint, a genuinely
/// unexpected failure) is deliberately left to propagate — silently
/// reclassifying an unrelated failure as "email already in use" would hide a
/// real bug.
/// </remarks>
public sealed class CreateUserExecutor : ICreateUserExecutor
{
    private const string EmailUniqueIndexName = "IX_users_tenant_id_normalized_email";

    private static readonly Error EmailAlreadyInUseError = new(
        IdentityErrorCodes.EmailAlreadyInUse, IdentityErrorCodes.EmailAlreadyInUse);

    private readonly IIdentityTransactionExecutor _transactionExecutor;

    public CreateUserExecutor(IIdentityTransactionExecutor transactionExecutor) =>
        _transactionExecutor = transactionExecutor;

    public async Task<Result<UserResult>> ExecuteAsync(
        Func<Task<Result<UserResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
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
