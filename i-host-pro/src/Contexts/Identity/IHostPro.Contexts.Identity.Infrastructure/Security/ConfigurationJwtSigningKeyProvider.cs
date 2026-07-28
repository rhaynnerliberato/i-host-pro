using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Development/single-key implementation of <see cref="IJwtSigningKeyProvider"/>
/// (ADR-012, Incremento 2 plan, Etapa 6): imports the RSA private key from
/// <see cref="JwtSigningKeyOptions.PrivateKeyPem"/> exactly once, at
/// construction — never per token — and caches the resulting
/// <see cref="JwtSigningKey"/>/<see cref="JwtValidationKey"/> for the
/// lifetime of this instance (registered as a singleton). Safe for concurrent
/// use: every exposed member is a read of an already-built, immutable value;
/// nothing is mutated after construction.
///
/// Production key storage (KMS/Key Vault/Vault) is a separate, not-yet-decided
/// concern (ADR-012) — swapping it later only requires a new
/// <see cref="IJwtSigningKeyProvider"/> implementation, never a change to
/// this interface or its consumers.
/// </summary>
public sealed class ConfigurationJwtSigningKeyProvider : IJwtSigningKeyProvider, IDisposable
{
    private readonly RSA _rsa;
    private readonly JwtSigningKey _signingKey;
    private readonly IReadOnlyCollection<JwtValidationKey> _validationKeys;

    public ConfigurationJwtSigningKeyProvider(IOptions<JwtSigningKeyOptions> options)
    {
        // Redundant with JwtSigningKeyOptionsValidator's ValidateOnStart check
        // by design: that check protects the real host at startup; this one
        // protects any code path (e.g. a unit test) that constructs this
        // provider directly, outside the validated DI pipeline.
        _rsa = JwtSigningKeyParser.ParseAndValidate(options.Value.PrivateKeyPem);

        var keyId = ComputeKeyId(_rsa);
        var securityKey = new RsaSecurityKey(_rsa) { KeyId = keyId };

        _signingKey = new JwtSigningKey(keyId, securityKey);
        _validationKeys = [new JwtValidationKey(keyId, securityKey)];
    }

    public JwtSigningKey GetCurrentSigningKey() => _signingKey;

    public IReadOnlyCollection<JwtValidationKey> GetValidationKeys() => _validationKeys;

    public void Dispose() => _rsa.Dispose();

    /// <summary>
    /// Deterministic, stable <c>kid</c> derived from the public key alone
    /// (SHA-256 of the DER-encoded SubjectPublicKeyInfo, base64url-encoded):
    /// the same key always yields the same id, and a different key always
    /// yields a different one — never a random or incrementing value, and
    /// never derived from the private key material itself.
    /// </summary>
    private static string ComputeKeyId(RSA rsa)
    {
        var publicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(publicKeyInfo);
        return Base64UrlEncoder.Encode(hash);
    }
}
