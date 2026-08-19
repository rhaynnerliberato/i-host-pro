namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record ConfigureWhatsAppTemplateMappingRequest(
    string TemplateKey,
    string ProviderTemplateName,
    string LanguageCode,
    IReadOnlyList<string> ParameterOrder);
