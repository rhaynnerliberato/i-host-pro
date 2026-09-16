using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Application-facing repository for <see cref="AirbnbEmailMessageReceipt"/> — tenant scoping handled by <c>BaseDbContext</c>'s Global Query Filter + RLS.</summary>
public interface IAirbnbEmailMessageReceiptRepository : IRepository<AirbnbEmailMessageReceipt, Guid>
{
    /// <summary>Ingestion-level idempotency check — mirrors the unique (tenant_id, graph_message_id) index.</summary>
    Task<bool> ExistsForCurrentTenantAsync(string graphMessageId, CancellationToken cancellationToken);
}
