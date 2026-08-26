using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbIntegrationRepository : IAirbnbIntegrationRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbIntegrationRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<AirbnbIntegration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AirbnbIntegrations.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<AirbnbIntegration?> GetForCurrentTenantAsync(CancellationToken cancellationToken) =>
        _dbContext.AirbnbIntegrations.FirstOrDefaultAsync(cancellationToken);

    public void Add(AirbnbIntegration aggregate) => _dbContext.AirbnbIntegrations.Add(aggregate);

    public void Update(AirbnbIntegration aggregate) => _dbContext.AirbnbIntegrations.Update(aggregate);

    public void Remove(AirbnbIntegration aggregate) => _dbContext.AirbnbIntegrations.Remove(aggregate);
}
