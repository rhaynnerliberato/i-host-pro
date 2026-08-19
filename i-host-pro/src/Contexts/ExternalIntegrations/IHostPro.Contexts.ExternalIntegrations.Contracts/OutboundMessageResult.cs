namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral outbound send outcome (ADR-021). Never carries the
/// provider's raw response body, HTTP status, access token, or any
/// provider-specific DTO — those stay in <c>ExternalIntegrations.Infrastructure</c>.
/// <see cref="ProviderMessageId"/> (e.g. a WhatsApp <c>wamid</c>) is a
/// provider identifier, not PII.
/// </summary>
public sealed record OutboundMessageResult(
    bool Accepted,
    string? ProviderMessageId,
    string? FailureCode,
    ProviderFailureCategory? FailureCategory);
