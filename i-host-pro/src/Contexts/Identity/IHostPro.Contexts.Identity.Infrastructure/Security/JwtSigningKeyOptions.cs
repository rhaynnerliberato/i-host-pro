namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// RSA private key material used to sign access tokens (ADR-012, Incremento 2
/// plan, Etapa 6). <see cref="PrivateKeyPem"/> MUST be supplied via an
/// environment variable or User Secrets — never via a committed appsettings
/// file, and it is never logged or echoed anywhere in this module.
///
/// Registered — and therefore bound/validated — exclusively by
/// <c>AddIdentityJwtIssuance</c>, called only from IHostPro.Api's composition
/// root. IHostPro.Worker never binds this type: it never issues or validates
/// a JWT and must never be required to hold the signing private key.
/// </summary>
public sealed class JwtSigningKeyOptions
{
    public const string SectionName = "Identity:Jwt:SigningKey";

    public string PrivateKeyPem { get; set; } = string.Empty;
}
