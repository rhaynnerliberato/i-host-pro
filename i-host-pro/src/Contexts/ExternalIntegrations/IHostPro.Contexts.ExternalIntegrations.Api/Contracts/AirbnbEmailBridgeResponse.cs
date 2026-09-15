using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>Never contains the token cache blob or the raw account identity — see <c>AirbnbEmailMailboxConnectionResult</c>'s own remarks.</summary>
public sealed record AirbnbEmailBridgeResponse(
    Guid TenantId,
    string? MailboxAddress,
    AirbnbEmailAuthorizationStatus AuthorizationStatus,
    bool IsEnabled,
    DateTimeOffset? LastAuthenticatedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
