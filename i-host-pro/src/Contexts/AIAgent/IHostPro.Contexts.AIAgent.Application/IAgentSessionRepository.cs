using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

public interface IAgentSessionRepository : IRepository<AgentSession, Guid>
{
    /// <summary>The single lookup <see cref="AgentSessionResolver"/> needs (mandate item 6 — one active AgentSession per Conversation, backstopped by a DB unique index, see the Infrastructure mapping).</summary>
    Task<AgentSession?> GetActiveByConversationIdAsync(Guid conversationId, CancellationToken cancellationToken);
}
