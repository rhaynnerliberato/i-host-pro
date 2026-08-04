using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Wraps <see cref="IPropertyManagementTransactionExecutor"/> for
/// <c>UpdatePropertyCommand</c> specifically, translating a caught unique-
/// constraint violation on <c>uq_properties_tenant_normalized_code</c> into
/// <see cref="Errors.PropertyManagementErrorCodes.PropertyCodeAlreadyExists"/>,
/// and a caught <c>DbUpdateConcurrencyException</c> into
/// <see cref="Errors.PropertyManagementErrorCodes.PropertyConcurrencyConflict"/>
/// — mirrors <c>IUpdateCondominiumExecutor</c>'s shape, extended with the
/// code-uniqueness translation Update (unlike Condominium's Update) also
/// needs. No bounded retry (Checkpoint 3 plan, item 18: "sem retry
/// automático").
/// </summary>
public interface IUpdatePropertyExecutor
{
    Task<Result<PropertyResult>> ExecuteAsync(
        Func<Task<Result<PropertyResult>>> operation, CancellationToken cancellationToken);
}
