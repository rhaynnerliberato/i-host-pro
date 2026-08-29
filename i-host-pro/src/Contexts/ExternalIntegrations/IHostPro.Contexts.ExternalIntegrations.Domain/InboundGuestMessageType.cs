namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Internal classification used by <c>MetaWebhookMessageProcessor</c>/
/// <c>WebhookMessageProcessingOutcome</c> (Fase 11, Checkpoint 1) — mirrors
/// <see cref="ProviderMessageStatus"/>'s own split from its Contracts-facing
/// counterpart exactly (ADR-021: Contracts never references Domain).
/// <c>WhatsAppWebhookMessageEventPublisher</c> maps this explicitly to
/// <c>IHostPro.Contexts.ExternalIntegrations.Contracts.InboundGuestMessageType</c>
/// — the two are deliberately separate types, never an implicit cast.
/// </summary>
public enum InboundGuestMessageType
{
    Text,
    Unsupported,
}
