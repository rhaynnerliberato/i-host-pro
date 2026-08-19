namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral outbound message request (ADR-021). <see cref="Destination"/>
/// and <see cref="RenderedContent"/> are PII/sensitive business payload —
/// they pass in-process only (never transported via RabbitMQ/Wolverine to
/// reach this boundary), are never persisted by External Integrations merely
/// for crossing it, and must never be logged.
/// </summary>
public sealed record OutboundMessageRequest(
    Guid TenantId,
    Guid MessageId,
    string Channel,
    string Destination,
    string RenderedContent,
    string IdempotencyKey);
