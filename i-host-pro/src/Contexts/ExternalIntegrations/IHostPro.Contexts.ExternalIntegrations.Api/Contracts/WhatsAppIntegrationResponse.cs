namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>
/// Never contains a secret value — only whether each secret reference is
/// configured (CP2.1 mandate §25/§27: <c>Configured</c> booleans, never the
/// reference itself; <c>Configured</c> is never conflated with
/// production-ready — see <c>IsEnabled</c>, which stays <c>false</c> for the
/// entire lifetime of this checkpoint).
/// </summary>
public sealed record WhatsAppIntegrationResponse(
    Guid TenantId,
    string? WabaId,
    string? PhoneNumberId,
    bool IsEnabled,
    bool AccessTokenConfigured,
    bool AppSecretConfigured,
    bool VerifyTokenConfigured,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
