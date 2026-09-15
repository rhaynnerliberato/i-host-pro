namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Why an interactive mailbox connection attempt did not succeed — never carries token material.</summary>
public enum AirbnbEmailAuthenticationFailureReason
{
    UserCancelled,
    ConsentDenied,
    AcquisitionFailed,
    AccountIdentityUnavailable,
}

/// <summary>
/// Result of <see cref="IAirbnbEmailAuthenticator.ConnectInteractiveAsync"/> —
/// carries only the non-secret account identity MSAL returns
/// (<see cref="HomeAccountId"/>/<see cref="AccountTenantId"/>/<see cref="MailboxAddress"/>/<see cref="GrantedScopes"/>)
/// plus the opaque token-cache bytes already persisted by
/// <see cref="IAirbnbEmailAuthenticator"/> via <see cref="IAirbnbEmailTokenCacheStore"/>.
/// Never exposes an access/refresh token — MSAL owns that lifecycle entirely.
/// </summary>
public sealed record AirbnbEmailAuthenticationOutcome
{
    public bool IsSuccess { get; }
    public string? HomeAccountId { get; }
    public string? AccountTenantId { get; }
    public string? MailboxAddress { get; }
    public string? GrantedScopes { get; }
    public AirbnbEmailAuthenticationFailureReason? FailureReason { get; }

    private AirbnbEmailAuthenticationOutcome(
        bool isSuccess, string? homeAccountId, string? accountTenantId, string? mailboxAddress, string? grantedScopes,
        AirbnbEmailAuthenticationFailureReason? failureReason)
    {
        IsSuccess = isSuccess;
        HomeAccountId = homeAccountId;
        AccountTenantId = accountTenantId;
        MailboxAddress = mailboxAddress;
        GrantedScopes = grantedScopes;
        FailureReason = failureReason;
    }

    public static AirbnbEmailAuthenticationOutcome Success(
        string homeAccountId, string? accountTenantId, string? mailboxAddress, string grantedScopes) =>
        new(true, homeAccountId, accountTenantId, mailboxAddress, grantedScopes, null);

    public static AirbnbEmailAuthenticationOutcome Failure(AirbnbEmailAuthenticationFailureReason reason) =>
        new(false, null, null, null, null, reason);
}
