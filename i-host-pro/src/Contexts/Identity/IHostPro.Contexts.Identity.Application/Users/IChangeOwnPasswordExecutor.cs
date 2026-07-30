using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the ChangeOwnPassword transactional operation (Incremento 3,
/// Checkpoint 9) — wraps the shared <c>IIdentityTransactionExecutor</c> like
/// <c>IUpdateUserExecutor</c>, translating a caught
/// <c>DbUpdateConcurrencyException</c> on the <c>Users</c> row's <c>xmin</c>
/// token into <see cref="IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.UserConcurrencyConflict"/>.
/// Also drains <see cref="IHostPro.Contexts.Identity.Application.Sessions.ISessionRevocationSignal"/>
/// into <see cref="IHostPro.Contexts.Identity.Application.ISessionRevocationCache"/>
/// after a successful commit, like <c>IBlockUserExecutor</c> — this command's
/// session-revocation cascade needs the same post-commit cache write.
///
/// No bounded retry, deliberately (Section 8 of the Checkpoint 9 decision:
/// "não implementar retry automático com dados de senha potencialmente
/// obsoletos") — unlike Block/AssignRole/RemoveRole's cascade, a concurrency
/// conflict here means a second concurrent write to the SAME user row raced
/// this one; retrying with the caller's already-validated (against
/// now-possibly-stale state) current/new password would be unsafe. Failing
/// the request and letting the caller decide whether to resubmit is correct,
/// exactly like <c>IUpdateUserExecutor</c>.
/// </summary>
public interface IChangeOwnPasswordExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
