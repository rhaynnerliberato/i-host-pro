using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Wraps <see cref="IPropertyManagementTransactionExecutor"/> for the three
/// lifecycle commands (<c>ActivatePropertyCommand</c>/
/// <c>DeactivatePropertyCommand</c>/<c>ArchivePropertyCommand</c> —
/// Checkpoint 4 plan, item 12), translating a caught
/// <c>DbUpdateConcurrencyException</c> into
/// <see cref="Errors.PropertyManagementErrorCodes.PropertyConcurrencyConflict"/>
/// — shared across all three, unlike Create/Update's own executors, since no
/// lifecycle transition can ever violate the code-uniqueness constraint
/// (none of them touch <c>Code</c>). No bounded retry.
/// </summary>
public interface ILifecyclePropertyExecutor
{
    Task<Result<PropertyResult>> ExecuteAsync(
        Func<Task<Result<PropertyResult>>> operation, CancellationToken cancellationToken);
}
