namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Runs an operation inside a tenant-aware, RLS-protected transaction,
/// publishing any collected Integration Event to this context's durable
/// outbox (<c>payments_messaging</c> schema) atomically with the domain
/// change — mirrors <c>GuestOperations.Application.IGuestOperationsTransactionExecutor</c>
/// exactly.
/// </summary>
public interface IPaymentsTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
