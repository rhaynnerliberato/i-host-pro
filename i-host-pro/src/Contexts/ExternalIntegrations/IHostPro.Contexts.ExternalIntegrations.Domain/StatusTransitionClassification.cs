namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Result of comparing a newly observed <see cref="ProviderMessageStatus"/>
/// against whatever was previously observed for the same message (Fase 9,
/// Checkpoint 2.3.2 — idempotency/monotonicity foundation).
/// </summary>
public enum StatusTransitionClassification
{
    /// <summary>A genuine forward-moving transition — should be applied.</summary>
    Forward,

    /// <summary>The same status observed again (e.g. a Meta webhook retry) — a no-op.</summary>
    Duplicate,

    /// <summary>
    /// An out-of-order or contradictory transition (e.g. Delivered arriving
    /// after Read, or Failed arriving after Delivered/Read) — a no-op,
    /// forward-only, never applied.
    /// </summary>
    Regression,
}
