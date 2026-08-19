namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Never carries a secret value — only whether each secret reference is
/// configured (CP2.1 mandate §25: "Preferir booleans conceituais... em vez
/// de revelar referências internas"). <see cref="CreatedAtUtc"/>/
/// <see cref="UpdatedAtUtc"/> are <c>null</c> when the tenant has never
/// configured a WhatsApp integration yet (see
/// <see cref="WhatsAppIntegrationResult.NotConfigured"/>) — a legitimate,
/// non-error state, never a 404.
/// </summary>
public sealed record WhatsAppIntegrationResult(
    Guid TenantId,
    string? WabaId,
    string? PhoneNumberId,
    bool IsEnabled,
    bool AccessTokenConfigured,
    bool AppSecretConfigured,
    bool VerifyTokenConfigured,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc)
{
    public static WhatsAppIntegrationResult NotConfigured(Guid tenantId) =>
        new(tenantId, WabaId: null, PhoneNumberId: null, IsEnabled: false,
            AccessTokenConfigured: false, AppSecretConfigured: false, VerifyTokenConfigured: false,
            CreatedAtUtc: null, UpdatedAtUtc: null);
}
