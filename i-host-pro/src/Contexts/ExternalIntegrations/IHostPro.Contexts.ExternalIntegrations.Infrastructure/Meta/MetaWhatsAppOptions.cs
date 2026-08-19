namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Bound from <c>ExternalIntegrations:WhatsApp:Meta</c> (Fase 9, Checkpoint
/// 2.2 — mandate §5/§13). Exactly one, explicit configuration source for the
/// Graph API version and HTTP timeout — never hardcoded in more than one
/// place (this class), and never re-derived per call site.
/// </summary>
public sealed class MetaWhatsAppOptions
{
    /// <summary>
    /// The Graph API version segment used in every outbound URL (e.g.
    /// <c>"v26.0"</c> — confirmed current at developers.facebook.com/docs/graph-api/changelog/
    /// as of this checkpoint's research; re-verify against the live Meta
    /// changelog before relying on this value staying current — Meta
    /// deprecates versions roughly every two years).
    /// </summary>
    public string GraphApiVersion { get; set; } = "v26.0";

    /// <summary>
    /// A single, conservative HTTP timeout for the outbound send call. No
    /// automatic retry exists this checkpoint (mandate §12/§13) — a timeout
    /// here always maps to <c>DeliveryOutcomeUnknown</c>, never a retry.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;
}
