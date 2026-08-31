using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

public sealed class AgentToolExecutionRepository : IAgentToolExecutionRepository
{
    private readonly AIAgentDbContext _dbContext;

    public AgentToolExecutionRepository(AIAgentDbContext dbContext) => _dbContext = dbContext;

    public Task<AgentToolExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AgentToolExecutions.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(AgentToolExecution aggregate) => _dbContext.AgentToolExecutions.Add(aggregate);

    public void Update(AgentToolExecution aggregate) => _dbContext.AgentToolExecutions.Update(aggregate);

    public void Remove(AgentToolExecution aggregate) => _dbContext.AgentToolExecutions.Remove(aggregate);
}
