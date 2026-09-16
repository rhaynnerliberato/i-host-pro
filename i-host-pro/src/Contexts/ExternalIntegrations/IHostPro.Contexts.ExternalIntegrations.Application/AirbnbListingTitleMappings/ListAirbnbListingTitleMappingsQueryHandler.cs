using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

public sealed class ListAirbnbListingTitleMappingsQueryHandler
    : IQueryHandler<ListAirbnbListingTitleMappingsQuery, IReadOnlyList<AirbnbListingTitleMappingResult>>
{
    private readonly IAirbnbListingTitleMappingRepository _repository;

    public ListAirbnbListingTitleMappingsQueryHandler(IAirbnbListingTitleMappingRepository repository) => _repository = repository;

    public async ValueTask<Result<IReadOnlyList<AirbnbListingTitleMappingResult>>> Handle(
        ListAirbnbListingTitleMappingsQuery query, CancellationToken cancellationToken)
    {
        var mappings = await _repository.ListForCurrentTenantAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AirbnbListingTitleMappingResult>>(
            mappings.Select(CreateAirbnbListingTitleMappingCommandHandler.ToResult).ToList());
    }
}
