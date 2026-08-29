namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// Durably publishes an <c>Accepted</c> <see cref="WebhookMessageProcessingOutcome"/>
/// as an <c>InboundGuestMessageReceived</c> Integration Event (Fase 11,
/// Checkpoint 1) — mirrors <see cref="IWhatsAppWebhookStatusEventPublisher"/>
/// exactly. Enqueue-and-commit happens atomically through this context's own
/// transactional outbox, before the webhook controller returns 2xx. A
/// transient failure here must propagate (never be swallowed) so the
/// controller's response becomes 5xx and Meta retries the delivery;
/// publishing again for an already-published outcome (Meta redelivery) is an
/// accepted, explicitly tolerated duplicate — idempotency is Communication's
/// own responsibility (lookup-before-create keyed on
/// TenantId/Channel/ProviderMessageId, mandate item 9).
/// </summary>
public interface IWhatsAppWebhookMessageEventPublisher
{
    /// <param name="outcome">Must have <see cref="WebhookMessageProcessingOutcome.Kind"/> == <see cref="WebhookMessageOutcomeKind.Accepted"/>.</param>
    Task PublishAsync(WebhookMessageProcessingOutcome outcome, CancellationToken cancellationToken);
}
