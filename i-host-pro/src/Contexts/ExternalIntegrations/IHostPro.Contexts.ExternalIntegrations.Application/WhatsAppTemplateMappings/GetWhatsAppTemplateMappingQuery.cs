using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

public sealed record GetWhatsAppTemplateMappingQuery(Guid TenantId, string TemplateKey) : IQuery<WhatsAppTemplateMappingResult>;
