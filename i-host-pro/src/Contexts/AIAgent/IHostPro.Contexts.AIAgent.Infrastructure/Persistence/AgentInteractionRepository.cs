using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

public sealed class AgentInteractionRepository : IAgentInteractionRepository
{
    private readonly AIAgentDbContext _dbContext;

    public AgentInteractionRepository(AIAgentDbContext dbContext) => _dbContext = dbContext;

    public Task<AgentInteraction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AgentInteractions.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<AgentInteraction?> GetByInboundMessageIdAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken) =>
        _dbContext.AgentInteractions.SingleOrDefaultAsync(
            i => i.TenantId == tenantId && i.InboundMessageId == inboundMessageId, cancellationToken);

    public void Add(AgentInteraction aggregate) => _dbContext.AgentInteractions.Add(aggregate);

    public void Update(AgentInteraction aggregate) => _dbContext.AgentInteractions.Update(aggregate);

    public void Remove(AgentInteraction aggregate) => _dbContext.AgentInteractions.Remove(aggregate);
}
