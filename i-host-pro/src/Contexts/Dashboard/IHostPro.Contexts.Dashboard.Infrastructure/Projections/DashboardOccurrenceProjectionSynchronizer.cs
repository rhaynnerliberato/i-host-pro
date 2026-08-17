using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Keeps <see cref="DashboardOccurrenceProjectionEntry"/> in sync with
/// Housekeeping's own <c>CleaningOccurrenceRegistered</c> (Fase 7,
/// Incremento 2 — Dashboard &amp; Reporting Foundation, Checkpoint 1).
/// Append-only — no out-of-order guard needed (there is no update path, only
/// insert-if-absent, mirroring the source aggregate's own append-only
/// nature).
/// </summary>
public sealed class DashboardOccurrenceProjectionSynchronizer : IIntegrationEventHandler<CleaningOccurrenceRegistered>
{
    private readonly DashboardDbContext _dbContext;
    private readonly IDashboardTransactionExecutor _executor;

    public DashboardOccurrenceProjectionSynchronizer(DashboardDbContext dbContext, IDashboardTransactionExecutor executor)
    {
        _dbContext = dbContext;
        _executor = executor;
    }

    /// <summary>Idempotent by construction: a redelivered event finds the row already present (harmless no-op).</summary>
    public Task HandleAsync(CleaningOccurrenceRegistered @event, CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(async () =>
        {
            var exists = await _dbContext.OccurrenceProjection
                .AnyAsync(o => o.TenantId == @event.TenantId && o.OccurrenceId == @event.OccurrenceId, cancellationToken);

            if (!exists)
            {
                _dbContext.OccurrenceProjection.Add(new DashboardOccurrenceProjectionEntry(
                    @event.TenantId, @event.OccurrenceId, @event.CleaningId, @event.OccurrenceType, @event.RegisteredAtUtc));
            }

            return true;
        }, cancellationToken);
}
