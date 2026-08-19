namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral outbound message request (ADR-021). <see cref="Destination"/>
/// and <see cref="TemplateVariables"/> are PII/sensitive business payload —
/// they pass in-process only (never transported via RabbitMQ/Wolverine to
/// reach this boundary), are never persisted by External Integrations merely
/// for crossing it, and must never be logged.
///
/// Fase 9, Checkpoint 2.2 (focused, authorized Contracts change — mandate
/// §22/§23): carries <see cref="TemplateKey"/> + <see cref="TemplateVariables"/>
/// (structural, ordered-by-name variables) instead of a pre-rendered free-text
/// body. A template-based provider (Meta Cloud API) must never parse rendered
/// text back into positional parameters — External Integrations resolves
/// <see cref="TemplateKey"/> to a provider template name/language/parameter
/// order internally (<c>WhatsAppTemplateMapping</c>,
/// <c>ExternalIntegrations.Infrastructure</c>); Communication must never pass
/// a provider-specific template name here. Communication may still render and
/// persist its own free-text copy for its own history — that rendered copy
/// simply never crosses this boundary.
/// </summary>
public sealed record OutboundMessageRequest(
    Guid TenantId,
    Guid MessageId,
    string Channel,
    string Destination,
    string TemplateKey,
    IReadOnlyDictionary<string, string> TemplateVariables,
    string IdempotencyKey);
