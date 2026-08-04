using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Wraps <see cref="IPropertyManagementTransactionExecutor"/> for
/// <c>LinkPropertyOwnerCommand</c> specifically, translating a caught
/// unique-constraint violation on <c>uq_property_owners_tenant_property_owner</c>
/// into <see cref="Errors.PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked"/>
/// (Checkpoint 5 plan, item 12).
///
/// Injected directly into <c>LinkPropertyOwnerCommandHandler</c> — NOT
/// wrapped around the whole command via a Mediator pipeline behavior, unlike
/// every other write command so far: the eligibility check against Identity
/// must complete, and close its own connection, BEFORE Property
/// Management's write transaction ever opens (Checkpoint 5 plan, item 6:
/// "não manter a transação de escrita de Property Management aberta
/// enquanto consulta Identity"). The handler therefore calls this executor
/// itself, only around the portion of its own logic that needs the write
/// transaction — after the eligibility check has already returned.
/// </summary>
public interface ILinkPropertyOwnerExecutor
{
    Task<Result<PropertyOwnerResult>> ExecuteAsync(
        Func<Task<Result<PropertyOwnerResult>>> operation, CancellationToken cancellationToken);
}
