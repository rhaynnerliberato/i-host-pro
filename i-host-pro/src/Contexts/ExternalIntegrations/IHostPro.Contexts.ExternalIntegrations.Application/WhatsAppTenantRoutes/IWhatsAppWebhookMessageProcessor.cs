namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// Parses an already signature-verified webhook body, resolves each inbound
/// message entry's tenant via <see cref="IWhatsAppTenantRouteResolver"/>, and
/// classifies/normalizes it (Fase 11, Checkpoint 1). Provider-specific
/// envelope parsing is entirely hidden behind this interface — the Api layer
/// never sees a Meta-shaped type (ADR-022 item 16, extended by this
/// checkpoint's own chronological amendment). Never creates a
/// <c>Conversation</c>/<c>Message</c> or publishes anything — that is
/// Communication's own consumer's job, reacting to the published event.
/// </summary>
public interface IWhatsAppWebhookMessageProcessor
{
    Task<IReadOnlyList<WebhookMessageProcessingOutcome>> ProcessAsync(
        ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken);
}
