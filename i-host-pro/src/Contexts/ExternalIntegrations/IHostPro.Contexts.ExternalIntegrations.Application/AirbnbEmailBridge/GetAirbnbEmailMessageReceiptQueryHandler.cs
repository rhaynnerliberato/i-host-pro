using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <inheritdoc cref="GetAirbnbEmailMessageReceiptQuery"/>
public sealed class GetAirbnbEmailMessageReceiptQueryHandler : IQueryHandler<GetAirbnbEmailMessageReceiptQuery, AirbnbEmailMessageReceiptResult?>
{
    private readonly IAirbnbEmailMessageReceiptRepository _repository;

    public GetAirbnbEmailMessageReceiptQueryHandler(IAirbnbEmailMessageReceiptRepository repository) => _repository = repository;

    public async ValueTask<Result<AirbnbEmailMessageReceiptResult?>> Handle(
        GetAirbnbEmailMessageReceiptQuery query, CancellationToken cancellationToken)
    {
        var receipt = await _repository.GetByIdAsync(query.ReceiptId, cancellationToken);
        if (receipt is null || receipt.TenantId != query.TenantId)
            return Result.Success<AirbnbEmailMessageReceiptResult?>(null);

        return Result.Success<AirbnbEmailMessageReceiptResult?>(new AirbnbEmailMessageReceiptResult(
            receipt.Id, receipt.ReceivedAtUtc, receipt.ProcessingStatus.ToString(), receipt.DetectedEventType,
            receipt.ParserVersion, receipt.FailureReason, receipt.UnmatchedListingTitle, receipt.ProcessedAtUtc, receipt.CreatedAtUtc));
    }
}
