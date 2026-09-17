using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed class GetAirbnbEmailProcessingSummaryQueryHandler
    : IQueryHandler<GetAirbnbEmailProcessingSummaryQuery, AirbnbEmailProcessingSummaryResult>
{
    private readonly IAirbnbEmailMessageReceiptRepository _repository;

    public GetAirbnbEmailProcessingSummaryQueryHandler(IAirbnbEmailMessageReceiptRepository repository) => _repository = repository;

    public async ValueTask<Result<AirbnbEmailProcessingSummaryResult>> Handle(
        GetAirbnbEmailProcessingSummaryQuery query, CancellationToken cancellationToken)
    {
        var counts = await _repository.CountByProcessingStatusForCurrentTenantAsync(cancellationToken);

        int CountFor(AirbnbEmailMessageProcessingStatus status) => counts.GetValueOrDefault(status);

        return Result.Success(new AirbnbEmailProcessingSummaryResult(
            query.TenantId,
            Pending: CountFor(AirbnbEmailMessageProcessingStatus.Pending),
            Processed: CountFor(AirbnbEmailMessageProcessingStatus.Processed),
            NeedsReview: CountFor(AirbnbEmailMessageProcessingStatus.NeedsReview),
            Failed: CountFor(AirbnbEmailMessageProcessingStatus.Failed),
            Ignored: CountFor(AirbnbEmailMessageProcessingStatus.Ignored)));
    }
}
