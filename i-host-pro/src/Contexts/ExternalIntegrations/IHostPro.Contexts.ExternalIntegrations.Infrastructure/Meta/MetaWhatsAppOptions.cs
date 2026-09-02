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

    /// <summary>
    /// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting) — Decision Gate
    /// amendment: HTTP circuit breaking via the official
    /// <c>Microsoft.Extensions.Http.Resilience</c> package (never a
    /// hand-rolled implementation, never retry/hedging/fallback/timeout —
    /// see <c>ExternalIntegrationsModuleExtensions</c>'s own wiring
    /// comment). A timeout/circuit-open still always maps to
    /// <c>DeliveryOutcomeUnknown</c>, never a resend — resending would risk
    /// a duplicate physical WhatsApp delivery.
    /// </summary>
    public MetaHttpCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>
/// A local copy of the same shape as AIAgent.Infrastructure's own
/// <c>HttpCircuitBreakerOptions</c> — deliberately NOT shared between the
/// two Infrastructure projects (they must never reference each other; each
/// Bounded Context's Infrastructure stays isolated, same discipline as
/// every other cross-cutting concern in this platform). No production-grade
/// threshold is decided by this checkpoint — every default is conservative
/// for dev/homologation (<c>ProductionCircuitBreakerThresholdsRequired=true</c>).
/// </summary>
public sealed class MetaHttpCircuitBreakerOptions
{
    public bool Enabled { get; set; } = true;
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 4;
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}
