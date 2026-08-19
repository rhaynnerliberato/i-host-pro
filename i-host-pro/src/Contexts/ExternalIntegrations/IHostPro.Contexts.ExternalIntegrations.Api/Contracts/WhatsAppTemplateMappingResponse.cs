namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record WhatsAppTemplateMappingResponse(
    Guid TenantId,
    string TemplateKey,
    string? ProviderTemplateName,
    string? LanguageCode,
    IReadOnlyList<string> ParameterOrder,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
