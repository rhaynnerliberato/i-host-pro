using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// One provider-neutral, sanitized outcome per inbound message entry found
/// in a verified webhook payload (Fase 11, Checkpoint 1) — mirrors
/// <see cref="WebhookStatusOutcomeKind"/>/<see cref="WebhookStatusProcessingOutcome"/>
/// exactly. Never carries the raw Meta envelope.
/// </summary>
public enum WebhookMessageOutcomeKind
{
    /// <summary>Signature valid, route known, message structurally well-formed (text or otherwise).</summary>
    Accepted,

    /// <summary>Signature valid, but the phone_number_id does not match any known tenant route.</summary>
    UnknownRoute,

    /// <summary>Signature valid, but the entry is structurally invalid (missing id/from/timestamp) — permanently unprocessable, never retried.</summary>
    Malformed,
}

public sealed record WebhookMessageProcessingOutcome(
    WebhookMessageOutcomeKind Kind,
    Guid? TenantId,
    string? ProviderMessageId,
    string? SenderPhoneNormalized,
    InboundGuestMessageType? MessageType,
    string? Text,
    DateTimeOffset? OccurredAtUtc);
