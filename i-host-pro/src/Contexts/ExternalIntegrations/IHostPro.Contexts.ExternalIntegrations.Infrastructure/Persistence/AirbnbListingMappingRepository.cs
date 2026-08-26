using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbListingMappingRepository : IAirbnbListingMappingRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbListingMappingRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<AirbnbListingMapping?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AirbnbListingMappings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<AirbnbListingMapping?> GetByExternalListingIdAsync(string externalListingId, CancellationToken cancellationToken) =>
        _dbContext.AirbnbListingMappings.FirstOrDefaultAsync(m => m.ExternalListingId == externalListingId, cancellationToken);

    public void Add(AirbnbListingMapping aggregate) => _dbContext.AirbnbListingMappings.Add(aggregate);

    public void Update(AirbnbListingMapping aggregate) => _dbContext.AirbnbListingMappings.Update(aggregate);

    public void Remove(AirbnbListingMapping aggregate) => _dbContext.AirbnbListingMappings.Remove(aggregate);
}
