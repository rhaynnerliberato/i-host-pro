using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed class GetAirbnbEmailBridgeStatusQueryHandler : IQueryHandler<GetAirbnbEmailBridgeStatusQuery, AirbnbEmailBridgeStatusResult>
{
    private readonly IAirbnbEmailMailboxConnectionRepository _repository;

    public GetAirbnbEmailBridgeStatusQueryHandler(IAirbnbEmailMailboxConnectionRepository repository) => _repository = repository;

    public async ValueTask<Result<AirbnbEmailBridgeStatusResult>> Handle(
        GetAirbnbEmailBridgeStatusQuery query, CancellationToken cancellationToken)
    {
        var connection = await _repository.GetForCurrentTenantAsync(cancellationToken);
        if (connection is null)
            return Result.Success(AirbnbEmailBridgeStatusResult.NotConfigured(query.TenantId));

        var status = connection.AuthorizationStatus switch
        {
            AirbnbEmailAuthorizationStatus.Connected => AirbnbEmailConnectionStatus.Connected,
            AirbnbEmailAuthorizationStatus.Error => AirbnbEmailConnectionStatus.Error,
            _ => AirbnbEmailConnectionStatus.Disconnected,
        };

        return Result.Success(new AirbnbEmailBridgeStatusResult(
            connection.TenantId, status, connection.IsEnabled, connection.LastAuthenticatedAtUtc, connection.MailboxAddress));
    }
}
