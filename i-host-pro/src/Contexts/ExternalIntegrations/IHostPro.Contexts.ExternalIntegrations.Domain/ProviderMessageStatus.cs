namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Provider-neutral normalization of an outbound message delivery status
/// (Fase 9, Checkpoint 2.3.2). Deliberately named without "Meta"/"Graph" —
/// real provider vocabulary (e.g. Meta's <c>played</c>, voice-message-only)
/// is translated into this set at the Infrastructure boundary, never leaked
/// upward. Only the four statuses CP2.3 MVP needs (text/template messages) —
/// <c>played</c> is deferred without a real requirement (mandate §18).
/// </summary>
public enum ProviderMessageStatus
{
    Sent,
    Delivered,
    Read,
    Failed,
}
