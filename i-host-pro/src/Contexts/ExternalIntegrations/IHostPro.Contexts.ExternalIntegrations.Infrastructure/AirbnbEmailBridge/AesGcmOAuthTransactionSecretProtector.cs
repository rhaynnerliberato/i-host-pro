using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// AES-256-GCM implementation of <see cref="IOAuthTransactionSecretProtector"/>.
/// Deliberately reuses the SAME master key as <see cref="AesGcmTokenCacheProtector"/>
/// (<c>ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64</c>)
/// rather than provisioning a second key: both protect secret material within
/// the same Airbnb Email Bridge trust boundary, and the PKCE verifier's
/// lifetime (10 minutes, single-use) is far shorter than the token cache's,
/// so sharing a rotation domain introduces no meaningful new risk.
/// </summary>
public sealed class AesGcmOAuthTransactionSecretProtector : IOAuthTransactionSecretProtector
{
    private const string KeyConfigurationKey = "ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64";

    private readonly IConfiguration _configuration;

    public AesGcmOAuthTransactionSecretProtector(IConfiguration configuration) => _configuration = configuration;

    public byte[] Protect(byte[] plaintext) => AesGcmPayloadCipher.Protect(ResolveKey(), plaintext);

    public byte[] Unprotect(byte[] protectedData) => AesGcmPayloadCipher.Unprotect(ResolveKey(), protectedData);

    private byte[] ResolveKey() => AesGcmPayloadCipher.ParseKey(KeyConfigurationKey, _configuration[KeyConfigurationKey]);
}
