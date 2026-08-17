namespace IHostPro.Contexts.Dashboard.Application;

/// <summary>
/// Runs a projection-synchronizer operation inside a tenant-aware,
/// RLS-protected transaction — mirrors
/// <c>Housekeeping.Application.IHousekeepingTransactionExecutor</c>/
/// <c>Reservations.Application.IReservationsTransactionExecutor</c> exactly.
/// Dashboard publishes no Integration Event of its own this increment
/// (Checkpoint 0 decision, §13), so the outbox-drain/publish step this
/// executor's real implementation performs is always a harmless no-op — kept
/// anyway because <c>IDbContextOutbox&lt;DashboardDbContext&gt;.SaveChangesAndFlushMessagesAsync</c>
/// is how <c>DashboardDbContext</c>'s own SaveChanges commits atomically
/// inside a Wolverine-managed transaction (same requirement already found
/// for Housekeeping/Reservations — <c>IDbContextOutbox&lt;T&gt;</c> only
/// resolves once its Ancillary store is enrolled).
/// </summary>
public interface IDashboardTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
