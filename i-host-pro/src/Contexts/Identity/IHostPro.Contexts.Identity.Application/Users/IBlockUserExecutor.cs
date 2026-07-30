using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the BlockUser transactional operation (Incremento 3, Checkpoint 7) —
/// mirrors <see cref="IAssignRoleExecutor"/>'s shape and rationale exactly,
/// including the Checkpoint 6 retry-safety correction applied from the start
/// (cleanup runs on every <see cref="System.Data.Common.DbException"/>-derived
/// <c>DbUpdateConcurrencyException</c>, including the final, exhausted
/// attempt): blocking a user cascades to all of their active sessions/refresh
/// tokens via <see cref="IUserSessionRevoker"/>, which can race a concurrent
/// Refresh on any one of those sessions the same way AssignRole/RemoveRole's
/// cascade can.
/// </summary>
public interface IBlockUserExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
