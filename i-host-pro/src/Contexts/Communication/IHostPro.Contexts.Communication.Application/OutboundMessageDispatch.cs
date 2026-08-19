namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// The minimal payload a Connector needs — <see cref="Destination"/> carries
/// the guest's real phone number in-memory only, for the duration of this
/// one call; it is never logged and never returned to the caller inside
/// <see cref="OutboundMessageDispatchResult"/>.
///
/// Fase 9, Checkpoint 2.2 (mandate §22/§23): <see cref="TenantId"/>/
/// <see cref="MessageId"/>/<see cref="TemplateKey"/>/<see cref="TemplateVariables"/>
/// were added so a real template-based connector (Meta) can build a
/// provider-neutral <c>ExternalIntegrations.Contracts.OutboundMessageRequest</c>
/// without ever parsing <see cref="Content"/> back into positional
/// parameters — <see cref="Content"/> remains Communication's own
/// pre-rendered free-text copy, kept for the fake connector and for
/// Communication's own persisted history (<c>Message.RenderedContent</c>).
/// </summary>
public sealed record OutboundMessageDispatch(
    Guid TenantId,
    Guid MessageId,
    string Destination,
    string TemplateKey,
    IReadOnlyDictionary<string, string> TemplateVariables,
    string Content,
    string IdempotencyKey);
