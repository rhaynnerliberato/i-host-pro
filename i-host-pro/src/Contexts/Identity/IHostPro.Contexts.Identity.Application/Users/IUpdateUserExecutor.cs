using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the UpdateUser transactional operation (Incremento 3, Checkpoint 8)
/// — wraps the shared <c>IIdentityTransactionExecutor</c>, additionally
/// translating two specific caught exceptions the handler cannot anticipate
/// from a normal read: a PostgreSQL unique-constraint violation on the
/// email-uniqueness index into <see cref="IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.EmailAlreadyInUse"/>
/// (mirrors <c>ICreateUserExecutor</c> exactly), and a
/// <c>DbUpdateConcurrencyException</c> on the <c>Users</c> row's <c>xmin</c>
/// token into <see cref="IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.UserConcurrencyConflict"/>.
///
/// No bounded retry, deliberately (Section 6: "não introduzir retry
/// automático para concorrência de atualização do usuário"): unlike
/// Block/AssignRole/RemoveRole's session-revocation cascade — where a
/// concurrency conflict is virtually always caused by ANOTHER command
/// touching the SAME row for an unrelated reason, safe to retry against
/// fresh state — a concurrency conflict here means a SECOND concurrent
/// PATCH of the SAME user raced this one; blindly retrying with the
/// caller's now-possibly-stale <c>fullName</c>/<c>email</c> could silently
/// overwrite the other request's change. Failing the request and letting
/// the caller decide whether to resubmit is the correct behavior.
/// </summary>
public interface IUpdateUserExecutor
{
    Task<Result<UserResult>> ExecuteAsync(Func<Task<Result<UserResult>>> operation, CancellationToken cancellationToken);
}
