namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// Parses an already signature-verified webhook body, resolves each status
/// entry's tenant via <see cref="IWhatsAppTenantRouteResolver"/>, and
/// normalizes recognized statuses (Fase 9, Checkpoint 2.3.2). Provider-
/// specific envelope parsing is entirely hidden behind this interface — the
/// Api layer never sees a Meta-shaped type (ADR-022 item 16/mandate §16).
/// Never mutates <c>Communication.Message</c> or publishes anything — that
/// is explicitly Checkpoint 2.3.3's job.
/// </summary>
public interface IWhatsAppWebhookStatusProcessor
{
    Task<IReadOnlyList<WebhookStatusProcessingOutcome>> ProcessAsync(
        ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken);
}
