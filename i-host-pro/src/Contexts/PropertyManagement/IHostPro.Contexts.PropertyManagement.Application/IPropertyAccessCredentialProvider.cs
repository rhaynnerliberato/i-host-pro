namespace IHostPro.Contexts.PropertyManagement.Application;

/// <summary>
/// Resolves an opaque secret reference
/// (<c>PropertyAccessConfiguration.AccessCredentialSecretReference</c>) to
/// the real guest-access credential value, at the boundary furthest from
/// persistence (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery).
/// Deliberately a NEW, standalone abstraction — NOT a reuse of
/// <c>ExternalIntegrations.Application.IWhatsAppCredentialProvider</c> (CP6.1
/// Decision Gate item 8): that interface exists exclusively to authenticate
/// this platform against an EXTERNAL provider API (Meta), owned by External
/// Integrations; a guest access credential is tenant-owned BUSINESS data
/// (closer in nature to <c>Payments.Domain.PixCharge.QrCodePayload</c>,
/// ADR-025, than to a provider API key), owned by Property Management, and
/// never used to call any external API.
///
/// Mirrors <c>IWhatsAppCredentialProvider</c>'s own Development/Production
/// split precedent (ADR-012 origin): Development resolves references via
/// User Secrets/environment variables/<c>IConfiguration</c>
/// (<c>DevelopmentPropertyAccessCredentialProvider</c>,
/// <c>PropertyManagement.Infrastructure</c>); the Production backend
/// (KMS/Key Vault/Vault) remains an explicitly open decision, blocked by the
/// still-undecided cloud provider (ADR-011) —
/// <c>ProductionAccessCredentialSecretBackendAvailable=false</c>. No
/// Production implementation exists in this checkpoint; resolving this
/// interface outside Development must fail loudly (no registration), never
/// silently succeed with a fabricated value.
///
/// A secret is never persisted, returned by any HTTP response, or logged —
/// callers must treat the returned value with the same care as the secret
/// itself.
/// </summary>
public interface IPropertyAccessCredentialProvider
{
    Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken);
}
