using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Application-facing repository for <see cref="WhatsAppIntegration"/> —
/// extends the generic <see cref="IRepository{TAggregate,TId}"/> with the
/// one lookup every command/query here actually needs (exactly one row per
/// tenant — see <c>WhatsAppIntegrationConfiguration</c>'s unique index).
/// Tenant scoping is handled transparently by <c>BaseDbContext</c>'s Global
/// Query Filter + RLS — never a caller-supplied <c>tenantId</c> parameter
/// here.
/// </summary>
public interface IWhatsAppIntegrationRepository : IRepository<WhatsAppIntegration, Guid>
{
    Task<WhatsAppIntegration?> GetForCurrentTenantAsync(CancellationToken cancellationToken);
}
