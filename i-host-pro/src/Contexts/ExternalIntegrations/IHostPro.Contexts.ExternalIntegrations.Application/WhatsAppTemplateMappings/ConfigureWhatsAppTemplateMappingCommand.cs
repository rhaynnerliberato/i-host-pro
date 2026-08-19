using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

/// <summary>
/// Creates or updates the tenant's mapping for one Communication
/// <c>TemplateKey</c> (upsert, keyed by TenantId+TemplateKey — never a
/// separate create/update pair). <see cref="ActorUserId"/> mirrors
/// <c>ConfigureWhatsAppIntegrationCommand.ActorUserId</c>'s own established
/// precedent — the caller's already-authenticated user id, read from claims
/// by the controller and passed as a plain primitive.
/// </summary>
public sealed record ConfigureWhatsAppTemplateMappingCommand(
    Guid TenantId,
    Guid ActorUserId,
    string TemplateKey,
    string ProviderTemplateName,
    string LanguageCode,
    IReadOnlyList<string> ParameterOrder) : ICommand<WhatsAppTemplateMappingResult>;
