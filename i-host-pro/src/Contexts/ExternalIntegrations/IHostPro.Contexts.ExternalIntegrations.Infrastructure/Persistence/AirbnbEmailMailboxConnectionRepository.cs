using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbEmailMailboxConnectionRepository : IAirbnbEmailMailboxConnectionRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbEmailMailboxConnectionRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<AirbnbEmailMailboxConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AirbnbEmailMailboxConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<AirbnbEmailMailboxConnection?> GetForCurrentTenantAsync(CancellationToken cancellationToken) =>
        _dbContext.AirbnbEmailMailboxConnections.FirstOrDefaultAsync(cancellationToken);

    public void Add(AirbnbEmailMailboxConnection aggregate) => _dbContext.AirbnbEmailMailboxConnections.Add(aggregate);

    public void Update(AirbnbEmailMailboxConnection aggregate) => _dbContext.AirbnbEmailMailboxConnections.Update(aggregate);

    public void Remove(AirbnbEmailMailboxConnection aggregate) => _dbContext.AirbnbEmailMailboxConnections.Remove(aggregate);
}
