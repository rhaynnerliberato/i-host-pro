using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Published when Meta notifies a status change for a previously-sent
/// outbound WhatsApp message (Fase 9, Checkpoint 2.3.3, ADR-022 item 14) —
/// the first Integration Event External Integrations ever publishes. Raised
/// only for a webhook status entry the signature verified, the tenant route
/// resolved, and <c>MetaWebhookStatusProcessor</c> classified as
/// <c>Accepted</c> — never for an unknown route or a malformed payload.
///
/// PII-safe by construction (mandate §5): never carries the recipient, the
/// phone number, the message body, the raw webhook payload, the WABA
/// payload, or any credential. <see cref="ProviderErrorCode"/> is Meta's own
/// short error code only — never the full textual error message.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>:
/// External Integrations owns no local aggregate representing a single
/// outbound message (<c>WhatsAppIntegration</c> is per-tenant configuration,
/// <c>WhatsAppTenantRoute</c> is the routing directory — neither identifies
/// one specific message). A fresh id is generated per event instead;
/// consumers must correlate by <see cref="ProviderMessageId"/>, the real
/// business key, never by <see cref="IntegrationEvent.AggregateId"/>.
///
/// <see cref="IntegrationEvent.CorrelationId"/>: this event originates from
/// an external Meta webhook call, not from a prior internal command/event,
/// and Meta's own payload carries no correlation id of its own (mandate §5:
/// "se já existir naturalmente" — it doesn't) — a fresh id is generated per
/// event, making each one the root of its own causal chain.
/// <see cref="IntegrationEvent.CausationId"/> is left unset for the same
/// reason. <see cref="IntegrationEvent.ActorType"/> is always
/// <c>"Integration"</c>.
/// </summary>
public sealed record WhatsAppMessageStatusChanged : IntegrationEvent
{
    public required string ProviderMessageId { get; init; }

    public required WhatsAppMessageProviderStatus Status { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Meta's own numeric error code (e.g. 131026) — never the full textual error message. Matches <c>WebhookStatusProcessingOutcome.ProviderErrorCode</c>'s own type exactly.</summary>
    public int? ProviderErrorCode { get; init; }
}
