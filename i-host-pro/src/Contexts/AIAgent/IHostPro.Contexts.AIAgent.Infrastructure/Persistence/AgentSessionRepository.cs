using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

public sealed class AgentSessionRepository : IAgentSessionRepository
{
    private readonly AIAgentDbContext _dbContext;

    public AgentSessionRepository(AIAgentDbContext dbContext) => _dbContext = dbContext;

    public Task<AgentSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AgentSessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<AgentSession?> GetActiveByConversationIdAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _dbContext.AgentSessions.SingleOrDefaultAsync(
            s => s.ConversationId == conversationId
                && (s.Status == AgentSessionStatus.Active || s.Status == AgentSessionStatus.Escalated),
            cancellationToken);

    public void Add(AgentSession aggregate) => _dbContext.AgentSessions.Add(aggregate);

    public void Update(AgentSession aggregate) => _dbContext.AgentSessions.Update(aggregate);

    public void Remove(AgentSession aggregate) => _dbContext.AgentSessions.Remove(aggregate);
}
