using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the RemoveRole transactional operation (Incremento 3, Checkpoint 6)
/// — mirrors <see cref="IAssignRoleExecutor"/>'s shape and rationale exactly,
/// including the bounded concurrency retry.
/// </summary>
public interface IRemoveRoleExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
