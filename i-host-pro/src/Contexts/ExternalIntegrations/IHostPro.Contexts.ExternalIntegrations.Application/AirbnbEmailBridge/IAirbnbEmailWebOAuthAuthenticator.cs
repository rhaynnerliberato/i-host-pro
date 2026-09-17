namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Performs the Web OAuth Authorization Code + PKCE flow for the Airbnb
/// Email Bridge (Web OAuth architecture gate) — a confidential client
/// (client secret), deliberately SEPARATE from <see cref="IAirbnbEmailAuthenticator"/>
/// (the local, public-client, interactive-only flow), which stays completely
/// unmodified. Both share the same encrypted token cache
/// (<see cref="IAirbnbEmailTokenCacheStore"/>) and the same domain method
/// (<see cref="Domain.AirbnbEmailMailboxConnection.Connect"/>) — no second
/// connection model, no duplicated persistence.
///
/// Never sees or persists the raw <c>state</c> value or its hash — those are
/// <see cref="IAirbnbEmailOAuthTransactionRepository"/>'s concern. This
/// interface only ever handles the PKCE verifier, which it protects at rest
/// immediately after generating it and decrypts only in memory, immediately
/// before use — the plaintext verifier never crosses this boundary.
/// </summary>
public interface IAirbnbEmailWebOAuthAuthenticator
{
    /// <summary>
    /// Generates a fresh PKCE pair and returns the Microsoft authorization
    /// URL to redirect the browser to, plus the (already-encrypted) verifier
    /// and the high-entropy <c>state</c> value the caller must persist (via
    /// <see cref="IAirbnbEmailOAuthTransactionRepository.CreatePending"/>,
    /// hashed — see <see cref="AirbnbEmailOAuthStateHasher"/>) before
    /// returning it to the browser. Pure/no I/O — returns <c>null</c> only
    /// when the Web OAuth confidential-client configuration
    /// (ClientId/ClientSecret/WebRedirectUri) is not yet set up.
    /// </summary>
    AirbnbEmailWebOAuthAuthorizationRequest? BuildAuthorizationRequest();

    /// <summary>
    /// Exchanges the authorization code for tokens and, on success, persists
    /// the mailbox connection exactly as the local flow does. Decrypts
    /// <paramref name="protectedPkceVerifier"/> only in memory for the
    /// duration of this call.
    /// </summary>
    Task<AirbnbEmailWebOAuthCallbackOutcome> CompleteAsync(
        Guid tenantId, string authorizationCode, byte[] protectedPkceVerifier, CancellationToken cancellationToken);
}

public sealed record AirbnbEmailWebOAuthAuthorizationRequest(string AuthorizationUrl, string State, byte[] ProtectedPkceVerifier);

public enum AirbnbEmailWebOAuthCallbackFailureReason
{
    ConsentDenied,
    AccountIdentityUnavailable,
    ExchangeFailed,
}

public sealed class AirbnbEmailWebOAuthCallbackOutcome
{
    public bool IsSuccess { get; }
    public AirbnbEmailWebOAuthCallbackFailureReason? FailureReason { get; }

    private AirbnbEmailWebOAuthCallbackOutcome(bool isSuccess, AirbnbEmailWebOAuthCallbackFailureReason? failureReason)
    {
        IsSuccess = isSuccess;
        FailureReason = failureReason;
    }

    public static AirbnbEmailWebOAuthCallbackOutcome Success() => new(true, null);

    public static AirbnbEmailWebOAuthCallbackOutcome Failure(AirbnbEmailWebOAuthCallbackFailureReason reason) => new(false, reason);
}
