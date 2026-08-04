using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Wraps <see cref="IPropertyManagementTransactionExecutor"/> for
/// <c>CreatePropertyCommand</c> specifically, translating a caught unique-
/// constraint violation on <c>uq_properties_tenant_normalized_code</c> into
/// <see cref="Errors.PropertyManagementErrorCodes.PropertyCodeAlreadyExists"/>
/// — mirrors <c>ICreateUserExecutor</c>'s shape. Never a generic
/// <c>DbUpdateException</c> translation (Checkpoint 3 plan, item 5/15).
/// </summary>
public interface ICreatePropertyExecutor
{
    Task<Result<PropertyResult>> ExecuteAsync(
        Func<Task<Result<PropertyResult>>> operation, CancellationToken cancellationToken);
}
