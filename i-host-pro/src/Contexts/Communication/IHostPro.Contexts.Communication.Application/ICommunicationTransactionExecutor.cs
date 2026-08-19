namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Runs an operation inside a tenant-aware, RLS-protected transaction —
/// mirrors <c>Housekeeping.Application.IHousekeepingTransactionExecutor</c>/
/// <c>Reservations.Application.IReservationsTransactionExecutor</c> in
/// shape, but never wraps <c>IDbContextOutbox&lt;CommunicationDbContext&gt;</c>:
/// Communication publishes no Integration Event of its own this checkpoint
/// (CP1 mandate §57 — default is not to, absent a real consumer), so a plain
/// <c>SaveChangesAsync</c> inside an RLS transaction is proportional — no
/// Wolverine ancillary-store enrollment needed for this context at all.
/// </summary>
public interface ICommunicationTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
