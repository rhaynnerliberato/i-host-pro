using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Published when Meta notifies an inbound message from a guest (Fase 11,
/// Checkpoint 1 — Inbound Conversation Foundation, ADR-022 chronological
/// amendment) — the second Integration Event External Integrations ever
/// publishes, mirroring <see cref="WhatsAppMessageStatusChanged"/>'s own
/// structure exactly. Raised only for a webhook <c>messages[]</c> entry the
/// signature verified, the tenant route resolved, and
/// <c>MetaWebhookMessageProcessor</c> classified as <c>Accepted</c> — never
/// for an unknown route or a malformed payload.
///
/// CP1 is TEXT ONLY: any other Meta message type (image/audio/video/
/// document/location/...) is classified <see cref="InboundGuestMessageType.Unsupported"/>
/// with <see cref="Text"/> left <c>null</c> — never downloaded, never
/// persisted, never modeled further (Documento 16 §31: voice/image/document
/// recognition are explicitly future scope).
///
/// PII-minimized by construction: carries <see cref="SenderPhoneNormalized"/>
/// (needed downstream by Communication to resolve a Reservation via
/// <c>IReservationByGuestPhoneReader</c>, ADR-029) and, for text messages
/// only, the message body itself — but never the raw webhook payload, the
/// WABA payload, the recipient's WhatsApp profile name, or any credential.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>:
/// same reasoning as <see cref="WhatsAppMessageStatusChanged"/> — External
/// Integrations owns no local aggregate representing a single inbound
/// message; a fresh id is generated per event, consumers correlate by
/// <see cref="ProviderMessageId"/>. <see cref="IntegrationEvent.CorrelationId"/>
/// is a fresh id per event for the same reason (Meta's payload carries no
/// correlation id of its own); <see cref="IntegrationEvent.CausationId"/> is
/// left unset. <see cref="IntegrationEvent.ActorType"/> is always
/// <c>"Integration"</c>.
/// </summary>
public sealed record InboundGuestMessageReceived : IntegrationEvent
{
    public required string ProviderMessageId { get; init; }

    public required string Channel { get; init; }

    /// <summary>Digits-only — never the raw Meta <c>wa_id</c>/<c>from</c> value verbatim (same rule <c>IReservationByGuestPhoneReader</c>'s caller must apply on the other side, ADR-029).</summary>
    public required string SenderPhoneNormalized { get; init; }

    public required InboundGuestMessageType MessageType { get; init; }

    /// <summary>Populated only when <see cref="MessageType"/> is <see cref="InboundGuestMessageType.Text"/>.</summary>
    public string? Text { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>CP1 is TEXT ONLY (mandate item 24) — every other Meta message type collapses into <see cref="Unsupported"/>, content never downloaded/persisted.</summary>
public enum InboundGuestMessageType
{
    Text,
    Unsupported,
}
