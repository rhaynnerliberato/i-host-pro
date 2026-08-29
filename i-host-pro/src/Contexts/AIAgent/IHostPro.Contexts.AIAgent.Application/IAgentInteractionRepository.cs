using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

public interface IAgentInteractionRepository : IRepository<AgentInteraction, Guid>
{
    /// <summary>Idempotency lookup (mandate item 36) — the business key is TenantId + InboundMessageId; the same ConversationMessageReceived/MessageId must never produce a second AgentInteraction.</summary>
    Task<AgentInteraction?> GetByInboundMessageIdAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken);
}
