using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

public sealed class CreateAirbnbListingTitleMappingCommandHandler
    : ICommandHandler<CreateAirbnbListingTitleMappingCommand, AirbnbListingTitleMappingResult>
{
    private readonly IAirbnbListingTitleMappingRepository _repository;
    private readonly TimeProvider _timeProvider;

    public CreateAirbnbListingTitleMappingCommandHandler(IAirbnbListingTitleMappingRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<AirbnbListingTitleMappingResult>> Handle(
        CreateAirbnbListingTitleMappingCommand command, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetByListingTitleAsync(command.ListingTitle, cancellationToken);
        if (existing is not null)
        {
            return Result.Failure<AirbnbListingTitleMappingResult>(
                new Error(AirbnbListingTitleMappingErrorCodes.DuplicateListingTitle, AirbnbListingTitleMappingErrorCodes.DuplicateListingTitle));
        }

        var mapping = AirbnbListingTitleMapping.Create(
            Guid.NewGuid(), command.TenantId, command.ListingTitle, command.PropertyId, _timeProvider.GetUtcNow());
        _repository.Add(mapping);

        return Result.Success(ToResult(mapping));
    }

    internal static AirbnbListingTitleMappingResult ToResult(AirbnbListingTitleMapping mapping) => new(
        mapping.Id, mapping.TenantId, mapping.ListingTitle, mapping.PropertyId, mapping.CreatedAtUtc, mapping.UpdatedAtUtc);
}
