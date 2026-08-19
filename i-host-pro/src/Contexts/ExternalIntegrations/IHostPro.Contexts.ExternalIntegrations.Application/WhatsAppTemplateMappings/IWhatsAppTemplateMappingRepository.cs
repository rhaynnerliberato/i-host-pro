using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

/// <summary>
/// Application-facing repository for <see cref="WhatsAppTemplateMapping"/> —
/// extends the generic <see cref="IRepository{TAggregate,TId}"/> with the one
/// lookup both the admin command/query and the real Meta provider adapter
/// actually need (exactly one row per tenant+TemplateKey — see
/// <c>WhatsAppTemplateMappingConfiguration</c>'s unique index). Tenant
/// scoping is handled transparently by <c>BaseDbContext</c>'s Global Query
/// Filter + RLS — never a caller-supplied <c>tenantId</c> parameter here.
/// </summary>
public interface IWhatsAppTemplateMappingRepository : IRepository<WhatsAppTemplateMapping, Guid>
{
    Task<WhatsAppTemplateMapping?> GetForCurrentTenantByTemplateKeyAsync(string templateKey, CancellationToken cancellationToken);
}
