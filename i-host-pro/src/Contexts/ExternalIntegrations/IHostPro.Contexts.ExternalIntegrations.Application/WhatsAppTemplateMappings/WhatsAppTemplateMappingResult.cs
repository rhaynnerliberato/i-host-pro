namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

/// <summary>
/// Never carries a secret — a template mapping has none. <see cref="CreatedAtUtc"/>/
/// <see cref="UpdatedAtUtc"/> are <c>null</c> when the tenant has never
/// configured a mapping for this <see cref="TemplateKey"/> yet (see
/// <see cref="NotConfigured"/>) — a legitimate, non-error state, never a 404.
/// </summary>
public sealed record WhatsAppTemplateMappingResult(
    Guid TenantId,
    string TemplateKey,
    string? ProviderTemplateName,
    string? LanguageCode,
    IReadOnlyList<string> ParameterOrder,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc)
{
    public static WhatsAppTemplateMappingResult NotConfigured(Guid tenantId, string templateKey) =>
        new(tenantId, templateKey, ProviderTemplateName: null, LanguageCode: null,
            ParameterOrder: Array.Empty<string>(), CreatedAtUtc: null, UpdatedAtUtc: null);
}
