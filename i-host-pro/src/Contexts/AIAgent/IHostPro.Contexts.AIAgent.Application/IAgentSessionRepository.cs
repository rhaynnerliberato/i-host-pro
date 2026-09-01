using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

public interface IAgentSessionRepository : IRepository<AgentSession, Guid>
{
    /// <summary>
    /// The single lookup <see cref="AgentSessionResolver"/> needs (mandate
    /// item 6 — one open AgentSession per Conversation, backstopped by a DB
    /// unique index, see the Infrastructure mapping). "Open" means
    /// <see cref="AgentSessionStatus.Active"/> OR (Fase 11, Checkpoint 6)
    /// <see cref="AgentSessionStatus.Escalated"/> — the resolver must reuse
    /// an Escalated session too, never create a second one while a real
    /// human handoff is still pending, or the suspended-session guard would
    /// be silently bypassed by a brand-new Active session.
    /// </summary>
    Task<AgentSession?> GetActiveByConversationIdAsync(Guid conversationId, CancellationToken cancellationToken);
}
