namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Result of a background (non-interactive) token acquisition for an
/// already-connected mailbox. <see cref="AccessToken"/> is only ever passed
/// directly to <see cref="IAirbnbEmailMessageSource"/> — never logged,
/// persisted, or returned from any Application-layer command/query result.
/// </summary>
public sealed record AirbnbEmailSilentAcquisitionOutcome
{
    public bool IsSuccess { get; }
    public string? AccessToken { get; }
    public bool ReauthorizationRequired { get; }

    private AirbnbEmailSilentAcquisitionOutcome(bool isSuccess, string? accessToken, bool reauthorizationRequired)
    {
        IsSuccess = isSuccess;
        AccessToken = accessToken;
        ReauthorizationRequired = reauthorizationRequired;
    }

    public static AirbnbEmailSilentAcquisitionOutcome Success(string accessToken) => new(true, accessToken, false);

    /// <param name="reauthorizationRequired">True when MSAL reports the cached account can no longer refresh silently (e.g. <c>MsalUiRequiredException</c>) — the connection must be marked as needing a fresh interactive connect, never retried silently in a loop.</param>
    public static AirbnbEmailSilentAcquisitionOutcome Failure(bool reauthorizationRequired) => new(false, null, reauthorizationRequired);
}
