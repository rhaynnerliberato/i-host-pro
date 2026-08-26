namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Runs a write Command's operation inside a tenant-aware, RLS-protected
/// transaction, publishing any collected Integration Event to this
/// context's durable outbox (<c>guest_operations_messaging</c> schema)
/// atomically with the domain change — mirrors
/// <c>Reservations.Application.IReservationsTransactionExecutor</c> exactly.
/// </summary>
public interface IGuestOperationsTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
