using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Creates or updates the tenant's single WhatsApp integration (upsert —
/// exactly one row per tenant, never a separate create/update pair). Never
/// touches <c>IsEnabled</c> — no command in this checkpoint can enable a
/// real integration (CP2.1 mandate §18).
/// </summary>
public sealed record ConfigureWhatsAppIntegrationCommand(
    Guid TenantId,
    string? WabaId,
    string? PhoneNumberId,
    string? AccessTokenSecretReference,
    string? AppSecretSecretReference,
    string? VerifyTokenSecretReference) : ICommand<WhatsAppIntegrationResult>;
