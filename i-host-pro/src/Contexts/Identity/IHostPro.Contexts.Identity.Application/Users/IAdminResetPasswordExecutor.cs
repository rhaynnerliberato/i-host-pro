using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the AdminResetPassword transactional operation (Incremento 3,
/// Checkpoint 9) — structurally identical to <see cref="IChangeOwnPasswordExecutor"/>:
/// wraps <c>IIdentityTransactionExecutor</c>, translates a caught
/// <c>DbUpdateConcurrencyException</c> into
/// <see cref="IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.UserConcurrencyConflict"/>
/// with no bounded retry, and drains
/// <see cref="IHostPro.Contexts.Identity.Application.Sessions.ISessionRevocationSignal"/>
/// into <see cref="IHostPro.Contexts.Identity.Application.ISessionRevocationCache"/>
/// after a successful commit. See <see cref="IChangeOwnPasswordExecutor"/>'s
/// doc comment for the full no-retry rationale (Section 8 of the Checkpoint 9
/// decision) — it applies verbatim here.
/// </summary>
public interface IAdminResetPasswordExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
