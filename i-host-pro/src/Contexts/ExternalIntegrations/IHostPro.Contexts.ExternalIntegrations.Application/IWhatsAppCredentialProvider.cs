namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <summary>
/// Resolves an opaque secret reference (<c>WhatsAppIntegration.AccessTokenSecretReference</c>/
/// <c>AppSecretSecretReference</c>/<c>VerifyTokenSecretReference</c>) to the
/// real secret value, at the boundary furthest from persistence — mirrors
/// <c>IJwtSigningKeyProvider</c>'s own precedent (ADR-012): Development
/// resolves references via User Secrets/environment variables/
/// <c>IConfiguration</c> (<c>DevelopmentWhatsAppCredentialProvider</c>,
/// <c>ExternalIntegrations.Infrastructure</c>); the Production backend
/// (KMS/Key Vault/Vault) remains an explicitly open decision, blocked by the
/// still-undecided cloud provider (ADR-011) — same boundary ADR-012 already
/// accepted for the JWT signing key. No Production implementation exists in
/// this checkpoint; Production is not, and must not appear to be, able to
/// activate real WhatsApp sending (CP2.1 mandate §12).
///
/// A secret is never persisted, returned by any HTTP response, or logged —
/// callers must treat the returned value with the same care as the secret
/// itself.
/// </summary>
public interface IWhatsAppCredentialProvider
{
    Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken);
}
