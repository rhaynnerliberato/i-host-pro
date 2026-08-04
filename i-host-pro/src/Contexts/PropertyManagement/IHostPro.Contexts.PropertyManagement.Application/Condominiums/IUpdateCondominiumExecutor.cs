using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Wraps <see cref="IPropertyManagementTransactionExecutor"/> for
/// <c>UpdateCondominiumCommand</c> specifically, translating a caught
/// <c>DbUpdateConcurrencyException</c> into
/// <see cref="Errors.PropertyManagementErrorCodes.CondominiumConcurrencyConflict"/>
/// — mirrors <c>IUpdateUserExecutor</c>'s shape. No bounded retry (Checkpoint
/// 2 plan, item 9: "sem retry automático").
/// </summary>
public interface IUpdateCondominiumExecutor
{
    Task<Result<CondominiumResult>> ExecuteAsync(
        Func<Task<Result<CondominiumResult>>> operation, CancellationToken cancellationToken);
}
