using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// AES-256-GCM implementation of <see cref="ITokenCacheProtector"/>. The
/// master key is resolved lazily, on first use, from
/// <c>ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64</c>
/// (via <see cref="IConfiguration"/> — .NET User Secrets or an environment
/// variable in Development, never committed) — deliberately NOT validated at
/// host startup, since the Airbnb Email Bridge is an optional feature no
/// tenant has connected yet; a missing key must not block Api/Worker startup
/// for every other developer (Fase 9 review item: "Do NOT implement cloud
/// storage now" / key source swap is a future, separate decision).
///
/// The actual AES-256-GCM payload format lives in <see cref="AesGcmPayloadCipher"/>,
/// shared with <see cref="AesGcmOAuthTransactionSecretProtector"/> (Web OAuth
/// architecture gate, item 20) — this class only resolves this Bounded
/// Context's token-cache-specific key and delegates the cipher work.
/// </summary>
public sealed class AesGcmTokenCacheProtector : ITokenCacheProtector
{
    private const string KeyConfigurationKey = "ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64";

    private readonly IConfiguration _configuration;

    public AesGcmTokenCacheProtector(IConfiguration configuration) => _configuration = configuration;

    public byte[] Protect(byte[] plaintext) => AesGcmPayloadCipher.Protect(ResolveKey(), plaintext);

    public byte[] Unprotect(byte[] protectedData) => AesGcmPayloadCipher.Unprotect(ResolveKey(), protectedData);

    private byte[] ResolveKey() => AesGcmPayloadCipher.ParseKey(KeyConfigurationKey, _configuration[KeyConfigurationKey]);
}
