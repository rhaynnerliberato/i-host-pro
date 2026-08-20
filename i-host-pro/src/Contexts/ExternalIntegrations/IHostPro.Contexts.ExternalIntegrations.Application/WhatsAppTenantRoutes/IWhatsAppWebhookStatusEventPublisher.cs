namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// Durably publishes an <c>Accepted</c> <see cref="WebhookStatusProcessingOutcome"/>
/// as a <c>WhatsAppMessageStatusChanged</c> Integration Event (Fase 9,
/// Checkpoint 2.3.3, ADR-022 item 13) — enqueue-and-commit happens atomically
/// through this context's own transactional outbox, before the webhook
/// controller returns 2xx (mandate §10/§11). A transient failure here must
/// propagate (never be swallowed) so the controller's response becomes 5xx
/// and Meta retries the delivery — publishing again for an already-published
/// outcome is an accepted, explicitly tolerated duplicate (mandate §12);
/// idempotency is Communication's own responsibility.
/// </summary>
public interface IWhatsAppWebhookStatusEventPublisher
{
    /// <param name="outcome">Must have <see cref="WebhookStatusProcessingOutcome.Kind"/> == <see cref="WebhookStatusOutcomeKind.Accepted"/>.</param>
    Task PublishAsync(WebhookStatusProcessingOutcome outcome, CancellationToken cancellationToken);
}
