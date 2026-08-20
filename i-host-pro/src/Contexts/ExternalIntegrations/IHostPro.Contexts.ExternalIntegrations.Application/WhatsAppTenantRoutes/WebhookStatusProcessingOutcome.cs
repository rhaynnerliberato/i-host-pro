using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// One provider-neutral, sanitized outcome per status entry found in a
/// verified webhook payload (Fase 9, Checkpoint 2.3.2). Never carries the
/// recipient phone number, message content, or the raw Meta envelope —
/// only identifiers/classification, matching the same PII-safety discipline
/// already established for the future Integration Event (ADR-021 item 10).
/// </summary>
public enum WebhookStatusOutcomeKind
{
    /// <summary>Signature valid, route known, status recognized and well-formed.</summary>
    Accepted,

    /// <summary>Signature valid, but the phone_number_id does not match any known tenant route.</summary>
    UnknownRoute,

    /// <summary>Signature valid, but the entry is structurally invalid (missing id/status/timestamp, or an unrecognized status) — permanently unprocessable, never retried.</summary>
    Malformed,
}

public sealed record WebhookStatusProcessingOutcome(
    WebhookStatusOutcomeKind Kind,
    Guid? TenantId,
    string? ProviderMessageId,
    ProviderMessageStatus? NormalizedStatus,
    DateTimeOffset? OccurredAtUtc,
    int? ProviderErrorCode);
