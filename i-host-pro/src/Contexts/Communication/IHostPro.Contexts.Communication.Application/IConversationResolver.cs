namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Get-or-create the single active <c>Conversation</c> for a
/// (TenantId, ReservationId, Channel) triple (Fase 11, Checkpoint 1 —
/// mandate item 19's cardinality default). Every existing outbound processor
/// calls this before <c>Message.Create</c> so every Message (both
/// directions) carries a real <c>ConversationId</c> — never a placeholder.
/// </summary>
public interface IConversationResolver
{
    Task<Guid> GetOrCreateActiveConversationIdAsync(
        Guid tenantId, Guid reservationId, string channel, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);
}
