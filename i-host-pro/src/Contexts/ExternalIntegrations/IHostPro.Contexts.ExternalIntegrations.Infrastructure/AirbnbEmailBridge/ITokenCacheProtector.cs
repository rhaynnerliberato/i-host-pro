namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Authenticated encryption for the Airbnb Email Bridge's MSAL token-cache
/// blob at rest. Infrastructure-only concern — <see cref="Application.AirbnbEmailBridge.IMicrosoftGraphTokenCacheStore"/>
/// is the only caller, and it never sees ciphertext.
/// </summary>
public interface ITokenCacheProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> into a self-describing, versioned payload.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Decrypts a payload produced by <see cref="Protect"/>.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The authentication tag does not match (wrong key, or the payload was tampered with).</exception>
    /// <exception cref="NotSupportedException">The payload's version byte is not one this implementation understands.</exception>
    byte[] Unprotect(byte[] protectedData);
}
