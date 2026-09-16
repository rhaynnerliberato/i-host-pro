using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbReservationParser;

internal sealed class FakeAirbnbListingTitleMappingRepository : IAirbnbListingTitleMappingRepository
{
    private readonly List<AirbnbListingTitleMapping> _mappings = [];

    public static FakeAirbnbListingTitleMappingRepository WithMapping(AirbnbListingTitleMapping mapping)
    {
        var repository = new FakeAirbnbListingTitleMappingRepository();
        repository._mappings.Add(mapping);
        return repository;
    }

    public Task<AirbnbListingTitleMapping?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.FirstOrDefault(m => m.Id == id));

    public Task<AirbnbListingTitleMapping?> GetByListingTitleAsync(string listingTitle, CancellationToken cancellationToken)
    {
        var normalized = AirbnbListingTitleMapping.Normalize(listingTitle);
        return Task.FromResult(_mappings.FirstOrDefault(m => m.ListingTitle == normalized));
    }

    public void Add(AirbnbListingTitleMapping aggregate) => _mappings.Add(aggregate);

    public void Update(AirbnbListingTitleMapping aggregate)
    {
    }

    public void Remove(AirbnbListingTitleMapping aggregate) => _mappings.Remove(aggregate);
}
