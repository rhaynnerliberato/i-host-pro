using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Never carries the token cache blob or the raw home account id — only what an API consumer needs to know the connection's state.</summary>
public sealed record AirbnbEmailMailboxConnectionResult(
    Guid TenantId,
    string? MailboxAddress,
    AirbnbEmailAuthorizationStatus AuthorizationStatus,
    bool IsEnabled,
    DateTimeOffset? LastAuthenticatedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
