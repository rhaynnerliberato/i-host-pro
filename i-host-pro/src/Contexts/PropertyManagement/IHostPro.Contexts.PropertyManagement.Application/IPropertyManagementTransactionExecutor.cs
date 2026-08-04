namespace IHostPro.Contexts.PropertyManagement.Application;

/// <summary>
/// Runs a Property Management write Command inside the tenant-aware RLS
/// transaction and, unlike the generic <c>ITenantAwareUnitOfWork</c>, also
/// flushes any Integration Event the handler staged on
/// <see cref="IIntegrationEventCollector"/> to this context's own durable
/// outbox (<c>property_management_messaging</c>) atomically with the domain
/// change, before committing — own copy of Identity's
/// <c>IIdentityTransactionExecutor</c> shape (Checkpoint 2 plan, item 12).
/// Declared here, in Application, so command handlers/executors depend only
/// on this abstraction — never on the concrete Infrastructure class, which
/// also carries EF Core/Wolverine dependencies Application must never see
/// directly.
/// </summary>
public interface IPropertyManagementTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
