using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>Minimal Operations/UX gate — never contains the token cache, HomeAccountId, or AccountTenantId.</summary>
public sealed record AirbnbEmailBridgeStatusResponse(
    Guid TenantId,
    AirbnbEmailConnectionStatus Status,
    bool IsEnabled,
    DateTimeOffset? LastAuthenticatedAtUtc,
    string? MailboxAddress);
