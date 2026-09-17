namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Authenticated encryption for the Web OAuth PKCE code verifier at rest,
/// while it sits in <c>airbnb_email_oauth_transactions</c> for the short
/// (10-minute) window between <c>oauth/start</c> and <c>oauth/callback</c>.
/// Kept separate from <see cref="ITokenCacheProtector"/> deliberately — that
/// interface's contract is token-cache-specific (Web OAuth architecture gate,
/// item 20: "do not semantically abuse ITokenCacheProtector"). Both
/// implementations share the same underlying AES-256-GCM primitive
/// (<see cref="AesGcmPayloadCipher"/>) — no duplicated cryptographic code.
/// </summary>
public interface IOAuthTransactionSecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The authentication tag does not match (wrong key, or the payload was tampered with).</exception>
    byte[] Unprotect(byte[] protectedData);
}
