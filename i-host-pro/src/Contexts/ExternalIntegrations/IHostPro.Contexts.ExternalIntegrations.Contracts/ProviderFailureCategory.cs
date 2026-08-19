namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral classification of a failed outbound send — mapped, in
/// Checkpoint 2.2, from whichever real provider is used (Meta Cloud API,
/// CP2.0 audit §43) to this stable, provider-agnostic set. Never the
/// provider's own raw error code/subcode.
///
/// <see cref="DeliveryOutcomeUnknown"/> (added Checkpoint 2.2, mandate
/// §27-33): the send outcome is technically ambiguous — a timeout or network
/// interruption after the HTTP request was already transmitted, where Meta
/// may or may not have accepted the message. This is NOT a "the provider
/// rejected it" outcome and NOT a new <c>MessageStatus</c> — the message
/// still transitions to <c>Failed</c>, carrying this category, and is never
/// automatically retried/resent (doing so could duplicate a message Meta
/// already accepted).
/// </summary>
public enum ProviderFailureCategory
{
    AuthenticationFailed,
    InvalidRecipient,
    InvalidTemplate,
    RateLimited,
    TransientProviderFailure,
    PermanentFailure,
    DeliveryOutcomeUnknown,
}
