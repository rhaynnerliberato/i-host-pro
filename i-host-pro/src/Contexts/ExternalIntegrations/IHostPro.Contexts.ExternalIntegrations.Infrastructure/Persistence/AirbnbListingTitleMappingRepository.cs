using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbListingTitleMappingRepository : IAirbnbListingTitleMappingRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbListingTitleMappingRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<AirbnbListingTitleMapping?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AirbnbListingTitleMappings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<AirbnbListingTitleMapping?> GetByListingTitleAsync(string listingTitle, CancellationToken cancellationToken)
    {
        var normalized = AirbnbListingTitleMapping.Normalize(listingTitle);
        return _dbContext.AirbnbListingTitleMappings.FirstOrDefaultAsync(m => m.ListingTitle == normalized, cancellationToken);
    }

    public void Add(AirbnbListingTitleMapping aggregate) => _dbContext.AirbnbListingTitleMappings.Add(aggregate);

    public void Update(AirbnbListingTitleMapping aggregate) => _dbContext.AirbnbListingTitleMappings.Update(aggregate);

    public void Remove(AirbnbListingTitleMapping aggregate) => _dbContext.AirbnbListingTitleMappings.Remove(aggregate);
}
