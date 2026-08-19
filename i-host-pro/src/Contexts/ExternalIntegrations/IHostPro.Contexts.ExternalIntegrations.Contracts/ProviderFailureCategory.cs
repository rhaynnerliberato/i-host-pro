namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral classification of a failed outbound send — mapped, in
/// Checkpoint 2.2, from whichever real provider is used (Meta Cloud API,
/// CP2.0 audit §43) to this stable, provider-agnostic set. Never the
/// provider's own raw error code/subcode.
/// </summary>
public enum ProviderFailureCategory
{
    AuthenticationFailed,
    InvalidRecipient,
    InvalidTemplate,
    RateLimited,
    TransientProviderFailure,
    PermanentFailure,
}
