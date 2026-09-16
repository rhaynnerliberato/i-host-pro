using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed class DisableAirbnbAutoPublicationCommandHandler
    : ICommandHandler<DisableAirbnbAutoPublicationCommand, AirbnbAutoPublicationStatusResult>
{
    private static readonly Error NotFoundError = new(AirbnbEmailBridgeErrorCodes.ConnectionNotFound, AirbnbEmailBridgeErrorCodes.ConnectionNotFound);
    private static readonly Error AlreadyDisabledError = new(AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyDisabled, AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyDisabled);

    private readonly IAirbnbEmailMailboxConnectionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public DisableAirbnbAutoPublicationCommandHandler(IAirbnbEmailMailboxConnectionRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<AirbnbAutoPublicationStatusResult>> Handle(
        DisableAirbnbAutoPublicationCommand command, CancellationToken cancellationToken)
    {
        var connection = await _repository.GetForCurrentTenantAsync(cancellationToken);
        if (connection is null)
            return Result.Failure<AirbnbAutoPublicationStatusResult>(NotFoundError);

        if (!connection.AutoPublishEnabled)
            return Result.Failure<AirbnbAutoPublicationStatusResult>(AlreadyDisabledError);

        connection.DisableAutoPublish(_timeProvider.GetUtcNow());

        return Result.Success(EnableAirbnbAutoPublicationCommandHandler.ToResult(connection));
    }
}
