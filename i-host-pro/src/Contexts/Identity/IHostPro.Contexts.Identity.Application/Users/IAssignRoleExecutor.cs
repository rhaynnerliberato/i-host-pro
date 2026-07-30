using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the AssignRole transactional operation (Incremento 3, Checkpoint 6)
/// — mirrors <c>IRevokeOwnSessionExecutor</c>'s shape exactly, including the
/// bounded retry on a concurrency conflict: this command's session-revocation
/// cascade mutates the same <c>Session</c>/<c>RefreshToken</c> rows a
/// concurrent Refresh on one of the target user's OTHER sessions could race
/// against. A command-specific executor is required (rather than the plain
/// <c>IIdentityTransactionExecutor</c>) because that generic executor does
/// not drain <see cref="ISessionRevocationSignal"/> or write
/// <see cref="ISessionRevocationCache"/> after commit.
/// </summary>
public interface IAssignRoleExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
