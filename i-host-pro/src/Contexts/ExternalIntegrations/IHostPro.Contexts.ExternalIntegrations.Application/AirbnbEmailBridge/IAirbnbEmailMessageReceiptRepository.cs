using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Application-facing repository for <see cref="AirbnbEmailMessageReceipt"/> — tenant scoping handled by <c>BaseDbContext</c>'s Global Query Filter + RLS.</summary>
public interface IAirbnbEmailMessageReceiptRepository : IRepository<AirbnbEmailMessageReceipt, Guid>
{
    /// <summary>Ingestion-level idempotency check — mirrors the unique (tenant_id, graph_message_id) index.</summary>
    Task<bool> ExistsForCurrentTenantAsync(string graphMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// Database-side aggregation (GROUP BY) for the Minimal Operations/UX
    /// gate's processing-summary read model — never loads individual receipt
    /// rows into memory just to count them. Absent statuses are simply
    /// missing from the returned dictionary (callers default to 0).
    /// </summary>
    Task<IReadOnlyDictionary<AirbnbEmailMessageProcessingStatus, int>> CountByProcessingStatusForCurrentTenantAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Paginated, filterable exception listing (Airbnb Email Operational
    /// Exception Resolution gate) — mirrors <c>ICleaningReader.ListAsync</c>'s
    /// own shape/defensive clamping. <paramref name="status"/> is matched
    /// case-insensitively against <see cref="AirbnbEmailMessageProcessingStatus"/>'s
    /// own names (an unrecognized value matches nothing, never throws);
    /// <paramref name="reasonCode"/> is an exact match against <c>FailureReason</c>.
    /// Ordered by <c>CreatedAtUtc DESC</c> then <c>Id</c> — most recent
    /// exception first.
    /// </summary>
    Task<PagedResult<AirbnbEmailMessageReceiptResult>> ListForCurrentTenantAsync(
        string? status, string? reasonCode, int page, int pageSize, CancellationToken cancellationToken);
}
