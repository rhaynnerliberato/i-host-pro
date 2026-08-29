namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Get-or-create the single active <c>AgentSession</c> for a Conversation
/// (Fase 11, Checkpoint 2 — mandate item 6's cardinality default). Mirrors
/// <c>Communication.Application.IConversationResolver</c> exactly.
/// </summary>
public interface IAgentSessionResolver
{
    Task<Guid> GetOrCreateActiveSessionIdAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);
}
