using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed class GetAirbnbAutoPublicationStatusQueryHandler
    : IQueryHandler<GetAirbnbAutoPublicationStatusQuery, AirbnbAutoPublicationStatusResult>
{
    private readonly IAirbnbEmailMailboxConnectionRepository _repository;

    public GetAirbnbAutoPublicationStatusQueryHandler(IAirbnbEmailMailboxConnectionRepository repository) => _repository = repository;

    public async ValueTask<Result<AirbnbAutoPublicationStatusResult>> Handle(
        GetAirbnbAutoPublicationStatusQuery query, CancellationToken cancellationToken)
    {
        var connection = await _repository.GetForCurrentTenantAsync(cancellationToken);

        return Result.Success(connection is null
            ? AirbnbAutoPublicationStatusResult.NotConfigured(query.TenantId)
            : new AirbnbAutoPublicationStatusResult(connection.TenantId, connection.AutoPublishEnabled, connection.AutoPublishNotBeforeUtc));
    }
}
