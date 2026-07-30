using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Runs the RevokeOwnSession transactional operation (Incremento 3,
/// Checkpoint 4) — mirrors <c>ILogoutExecutor</c> exactly, including the
/// bounded retry on a concurrency conflict on a refresh token's row racing
/// against a concurrent Refresh (same race <c>LogoutExecutor</c>/
/// <c>RefreshTokenExchangeExecutor</c> already guard against: this command
/// mutates the exact same rows a Logout would — the target session and its
/// active refresh tokens). A command-specific executor is required here
/// (rather than the plain <c>IIdentityTransactionExecutor</c>) because that
/// generic executor does not drain <see cref="ISessionRevocationSignal"/> or
/// write <see cref="ISessionRevocationCache"/> after commit — see this
/// project's Checkpoint 4 report for the full rationale.
///
/// Declared here, in Application, so <c>RevokeOwnSessionTenantAwareBehavior</c>
/// and <c>RevokeOwnSessionCommandHandler</c>'s only caller depend on this
/// abstraction — never on the concrete Infrastructure class.
/// </summary>
public interface IRevokeOwnSessionExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
