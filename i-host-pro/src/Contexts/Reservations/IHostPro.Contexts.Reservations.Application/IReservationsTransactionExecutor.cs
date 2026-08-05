namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// Runs a write Command's operation inside a tenant-aware, RLS-protected
/// transaction, publishing any collected Integration Event to this
/// context's durable outbox (<c>reservations_messaging</c> schema)
/// atomically with the domain change — mirrors
/// <c>PropertyManagement.Application.IPropertyManagementTransactionExecutor</c>
/// exactly.
/// </summary>
public interface IReservationsTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
