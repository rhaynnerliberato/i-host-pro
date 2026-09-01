using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

public sealed class AgentHumanHandoffRepository : IAgentHumanHandoffRepository
{
    private readonly AIAgentDbContext _dbContext;

    public AgentHumanHandoffRepository(AIAgentDbContext dbContext) => _dbContext = dbContext;

    public void Add(AgentHumanHandoff handoff) => _dbContext.AgentHumanHandoffs.Add(handoff);

    public void Update(AgentHumanHandoff handoff) => _dbContext.AgentHumanHandoffs.Update(handoff);

    public Task<AgentHumanHandoff?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.AgentHumanHandoffs.FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

    public Task<AgentHumanHandoff?> GetActiveByAgentSessionIdAsync(Guid agentSessionId, CancellationToken cancellationToken) =>
        _dbContext.AgentHumanHandoffs.FirstOrDefaultAsync(
            h => h.AgentSessionId == agentSessionId
                && (h.Status == AgentHumanHandoffStatus.Requested || h.Status == AgentHumanHandoffStatus.Notified),
            cancellationToken);
}
