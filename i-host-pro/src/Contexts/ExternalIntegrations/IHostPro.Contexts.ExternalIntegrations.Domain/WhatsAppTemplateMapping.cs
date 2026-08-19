using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Maps one of Communication's own provider-neutral <c>TemplateKey</c>s
/// (e.g. <c>RESERVATION_CONFIRMATION</c>) to the Meta-approved WhatsApp
/// template that actually sends it (Fase 9, Checkpoint 2.2 — mandate
/// §17-20). Deliberately minimal — models only what the first real template
/// needs, never a generic template engine: <see cref="ProviderTemplateName"/>
/// + <see cref="LanguageCode"/> identify the Meta template, and
/// <see cref="ParameterOrder"/> is the ordered list of Communication's own
/// template variable names, positionally mapped to the Meta template body's
/// numbered parameters — the provider adapter substitutes each variable's
/// rendered value into that position without ever parsing free text back
/// into parameters.
///
/// Tenant-owned, RLS-protected, unique on (TenantId, TemplateKey) — never a
/// second mapping for the same key within one tenant.
/// </summary>
public sealed class WhatsAppTemplateMapping : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string TemplateKey { get; private set; } = null!;
    public string ProviderTemplateName { get; private set; } = null!;
    public string LanguageCode { get; private set; } = null!;
    public IReadOnlyList<string> ParameterOrder { get; private set; } = Array.Empty<string>();
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private WhatsAppTemplateMapping()
    {
        // EF Core materialization.
    }

    private WhatsAppTemplateMapping(
        Guid id, Guid tenantId, string templateKey, string providerTemplateName, string languageCode,
        IReadOnlyList<string> parameterOrder, DateTimeOffset createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        TemplateKey = templateKey;
        ProviderTemplateName = providerTemplateName;
        LanguageCode = languageCode;
        ParameterOrder = parameterOrder;
        CreatedAtUtc = createdAtUtc;
    }

    public static WhatsAppTemplateMapping Create(
        Guid id, Guid tenantId, string templateKey, string providerTemplateName, string languageCode,
        IReadOnlyList<string> parameterOrder, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
            throw new ArgumentException("Template key cannot be empty.", nameof(templateKey));
        if (string.IsNullOrWhiteSpace(providerTemplateName))
            throw new ArgumentException("Provider template name cannot be empty.", nameof(providerTemplateName));
        if (string.IsNullOrWhiteSpace(languageCode))
            throw new ArgumentException("Language code cannot be empty.", nameof(languageCode));

        return new WhatsAppTemplateMapping(
            id, tenantId, templateKey, providerTemplateName, languageCode, parameterOrder ?? Array.Empty<string>(), createdAtUtc);
    }

    public void UpdateMapping(
        string providerTemplateName, string languageCode, IReadOnlyList<string> parameterOrder, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(providerTemplateName))
            throw new ArgumentException("Provider template name cannot be empty.", nameof(providerTemplateName));
        if (string.IsNullOrWhiteSpace(languageCode))
            throw new ArgumentException("Language code cannot be empty.", nameof(languageCode));

        ProviderTemplateName = providerTemplateName;
        LanguageCode = languageCode;
        ParameterOrder = parameterOrder ?? Array.Empty<string>();
        UpdatedAtUtc = updatedAtUtc;
    }
}
