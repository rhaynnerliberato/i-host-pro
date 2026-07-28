using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// The key currently used to sign newly issued access tokens. Wraps the
/// private RSA key material — never exposed, logged, or serialized beyond
/// what <see cref="Microsoft.IdentityModel.Tokens.SigningCredentials"/>
/// itself requires internally.
/// </summary>
public sealed class JwtSigningKey
{
    public string KeyId { get; }
    public SigningCredentials SigningCredentials { get; }

    public JwtSigningKey(string keyId, RsaSecurityKey securityKey)
    {
        KeyId = keyId;
        SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
    }
}

/// <summary>
/// A public key that should currently be accepted when validating an
/// already-issued access token's signature.
/// </summary>
public sealed class JwtValidationKey
{
    public string KeyId { get; }
    public RsaSecurityKey SecurityKey { get; }

    public JwtValidationKey(string keyId, RsaSecurityKey securityKey)
    {
        KeyId = keyId;
        SecurityKey = securityKey;
    }
}

/// <summary>
/// Source of the RSA key material used to sign and validate access tokens
/// (ADR-012, Incremento 2 plan, Etapa 6). Deliberately shaped to support
/// future key rotation without a contract change: a single "current" key is
/// used for signing, while <see cref="GetValidationKeys"/> can return more
/// than one entry once rotation exists (the current key plus any previous
/// key still inside its overlap window) — today it returns exactly one.
/// </summary>
public interface IJwtSigningKeyProvider
{
    JwtSigningKey GetCurrentSigningKey();

    IReadOnlyCollection<JwtValidationKey> GetValidationKeys();
}
