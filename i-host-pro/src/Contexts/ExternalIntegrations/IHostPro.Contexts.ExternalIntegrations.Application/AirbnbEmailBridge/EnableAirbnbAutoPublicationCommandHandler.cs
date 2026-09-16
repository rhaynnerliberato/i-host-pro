using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Every precondition is checked HERE, against a precise error code, before
/// <see cref="AirbnbEmailMailboxConnection.EnableAutoPublish"/> is ever
/// called - mirrors <c>EnableWhatsAppIntegrationCommandHandler</c>'s own
/// division of responsibility.
/// </summary>
public sealed class EnableAirbnbAutoPublicationCommandHandler
    : ICommandHandler<EnableAirbnbAutoPublicationCommand, AirbnbAutoPublicationStatusResult>
{
    private static readonly Error NotFoundError = new(AirbnbEmailBridgeErrorCodes.ConnectionNotFound, AirbnbEmailBridgeErrorCodes.ConnectionNotFound);
    private static readonly Error MailboxNotConnectedError = new(AirbnbEmailBridgeErrorCodes.AutoPublicationMailboxNotConnected, AirbnbEmailBridgeErrorCodes.AutoPublicationMailboxNotConnected);
    private static readonly Error AlreadyEnabledError = new(AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyEnabled, AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyEnabled);
    private static readonly Error CutoffRequiredError = new(AirbnbEmailBridgeErrorCodes.AutoPublicationCutoffRequired, AirbnbEmailBridgeErrorCodes.AutoPublicationCutoffRequired);

    private readonly IAirbnbEmailMailboxConnectionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public EnableAirbnbAutoPublicationCommandHandler(IAirbnbEmailMailboxConnectionRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<AirbnbAutoPublicationStatusResult>> Handle(
        EnableAirbnbAutoPublicationCommand command, CancellationToken cancellationToken)
    {
        if (command.NotBeforeUtc is null)
            return Result.Failure<AirbnbAutoPublicationStatusResult>(CutoffRequiredError);

        var connection = await _repository.GetForCurrentTenantAsync(cancellationToken);
        if (connection is null)
            return Result.Failure<AirbnbAutoPublicationStatusResult>(NotFoundError);

        if (connection.AuthorizationStatus != AirbnbEmailAuthorizationStatus.Connected)
            return Result.Failure<AirbnbAutoPublicationStatusResult>(MailboxNotConnectedError);

        if (connection.AutoPublishEnabled)
            return Result.Failure<AirbnbAutoPublicationStatusResult>(AlreadyEnabledError);

        connection.EnableAutoPublish(command.NotBeforeUtc.Value, _timeProvider.GetUtcNow());

        return Result.Success(ToResult(connection));
    }

    internal static AirbnbAutoPublicationStatusResult ToResult(AirbnbEmailMailboxConnection connection) =>
        new(connection.TenantId, connection.AutoPublishEnabled, connection.AutoPublishNotBeforeUtc);
}
