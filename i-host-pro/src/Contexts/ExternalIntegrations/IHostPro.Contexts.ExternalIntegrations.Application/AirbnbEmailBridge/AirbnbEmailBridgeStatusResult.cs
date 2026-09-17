namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Airbnb Email Bridge Minimal Operations/UX gate — the minimal mailbox
/// connection read model for the operator's status screen. Never carries
/// HomeAccountId, AccountTenantId, token cache, or any other MSAL/internal
/// identifier — <see cref="MailboxAddress"/> is the one piece of account
/// identity approved for this tenant-scoped operational view (the operator
/// needs to know which mailbox is connected).
/// </summary>
public sealed record AirbnbEmailBridgeStatusResult(
    Guid TenantId, AirbnbEmailConnectionStatus Status, bool IsEnabled, DateTimeOffset? LastAuthenticatedAtUtc, string? MailboxAddress)
{
    public static AirbnbEmailBridgeStatusResult NotConfigured(Guid tenantId) =>
        new(tenantId, AirbnbEmailConnectionStatus.NotConfigured, IsEnabled: false, LastAuthenticatedAtUtc: null, MailboxAddress: null);
}
