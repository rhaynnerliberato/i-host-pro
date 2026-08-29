namespace IHostPro.Contexts.Communication.Contracts;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — ADR-030, synchronous
/// exception #14 (Architecture Principles §14). AI Agent may consult
/// Communication EXCLUSIVELY for a sanitized, chronological history of a
/// Conversation's messages, through this contract — implemented only in
/// <c>Communication.Infrastructure</c>, never any other layer.
///
/// Deliberately does NOT return the <c>Message</c> aggregate, a
/// <c>Reservation</c> reference, provider identifiers/status, destination,
/// failure details, guest phone, or any credential/PIX QR payload — see
/// <see cref="ConversationHistoryMessage"/>'s own minimal shape.
/// </summary>
public interface IConversationHistoryReader
{
    Task<IReadOnlyList<ConversationHistoryMessage>> GetHistoryAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal per-message projection (ADR-030) — <see cref="Content"/> is
/// already sanitized by the reader: a message whose persisted content is the
/// fixed <c>"[SENSITIVE CONTENT REDACTED]"</c> marker (or, for a PIX QR
/// delivery, a content the reader itself redacts before returning — see the
/// Infrastructure implementation's own doc comment) is returned exactly as
/// that marker, never reconstructed.
/// </summary>
public sealed record ConversationHistoryMessage(
    Guid MessageId,
    ConversationMessageDirection Direction,
    string Content,
    DateTimeOffset OccurredAtUtc);

/// <summary>Contracts' own copy of Communication.Domain's <c>MessageDirection</c> — never shared directly (ADR-021 Domain/Contracts type-split precedent).</summary>
public enum ConversationMessageDirection
{
    Inbound,
    Outbound,
}
