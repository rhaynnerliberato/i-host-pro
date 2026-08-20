namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral status carried by <see cref="WhatsAppMessageStatusChanged"/>
/// (Fase 9, Checkpoint 2.3.3, ADR-022 item 14). Deliberately a separate type
/// from <c>ExternalIntegrations.Domain.ProviderMessageStatus</c> — Contracts
/// never references Domain (ADR-021) — and deliberately closed to the
/// current scope: no <c>Played</c>, no framework for future providers
/// (mandate §6). A Meta <c>played</c> webhook or any unrecognized status
/// never reaches this type — <c>MetaWebhookStatusProcessor</c> already
/// classifies those as ignored before anything is published.
/// </summary>
public enum WhatsAppMessageProviderStatus
{
    Sent,
    Delivered,
    Read,
    Failed,
}
