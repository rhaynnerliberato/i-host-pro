using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

public sealed class AgentPendingActionRepository : IAgentPendingActionRepository
{
    private readonly AIAgentDbContext _dbContext;

    public AgentPendingActionRepository(AIAgentDbContext dbContext) => _dbContext = dbContext;

    public void Add(AgentPendingAction pendingAction) => _dbContext.AgentPendingActions.Add(pendingAction);

    public void Update(AgentPendingAction pendingAction) => _dbContext.AgentPendingActions.Update(pendingAction);

    public Task<AgentPendingAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.AgentPendingActions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<AgentPendingAction?> GetActiveByAgentSessionIdAsync(Guid agentSessionId, CancellationToken cancellationToken) =>
        _dbContext.AgentPendingActions.FirstOrDefaultAsync(
            a => a.AgentSessionId == agentSessionId
                && (a.Status == AgentPendingActionStatus.Proposed || a.Status == AgentPendingActionStatus.Confirmed),
            cancellationToken);
}
