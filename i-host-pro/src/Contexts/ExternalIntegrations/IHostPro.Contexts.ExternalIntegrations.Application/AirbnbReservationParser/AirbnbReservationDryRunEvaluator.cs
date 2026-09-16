using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

public sealed class AirbnbReservationDryRunEvaluator : IAirbnbReservationDryRunEvaluator
{
    private readonly IAirbnbReservationReminderParser _parser;
    private readonly IAirbnbListingTitleMappingRepository _listingTitleMappingRepository;

    public AirbnbReservationDryRunEvaluator(
        IAirbnbReservationReminderParser parser, IAirbnbListingTitleMappingRepository listingTitleMappingRepository)
    {
        _parser = parser;
        _listingTitleMappingRepository = listingTitleMappingRepository;
    }

    public async Task<AirbnbReservationDryRunOutcome> EvaluateAsync(
        string subject, string body, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        var parseResult = _parser.Parse(subject, body, receivedAtUtc);
        if (!parseResult.IsSuccess)
            return AirbnbReservationDryRunOutcome.ParseFailed(parseResult.FailureReason!.Value);

        var mapping = await _listingTitleMappingRepository.GetByListingTitleAsync(parseResult.ListingName!, cancellationToken);
        if (mapping is null)
            return AirbnbReservationDryRunOutcome.PropertyNotResolved();

        return AirbnbReservationDryRunOutcome.Ready(mapping.PropertyId);
    }
}
