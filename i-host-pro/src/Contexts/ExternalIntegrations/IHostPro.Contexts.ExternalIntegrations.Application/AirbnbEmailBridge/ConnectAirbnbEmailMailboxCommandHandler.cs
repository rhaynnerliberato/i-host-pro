using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Runs the interactive Microsoft authentication (via <see cref="IAirbnbEmailAuthenticator"/>,
/// local-first only — see that interface's own remarks) and, on success,
/// reads back the connection <see cref="IAirbnbEmailAuthenticator"/> itself
/// already updated (it owns writing <see cref="Domain.AirbnbEmailMailboxConnection.Connect"/>
/// and the encrypted token cache, since both require the account identity
/// and cache bytes only MSAL produces). This handler's only job is
/// authorization-outcome-to-Result mapping and building the response DTO.
/// </summary>
public sealed class ConnectAirbnbEmailMailboxCommandHandler
    : ICommandHandler<ConnectAirbnbEmailMailboxCommand, AirbnbEmailMailboxConnectionResult>
{
    private readonly IAirbnbEmailAuthenticator _authenticator;
    private readonly IAirbnbEmailMailboxConnectionRepository _repository;

    public ConnectAirbnbEmailMailboxCommandHandler(
        IAirbnbEmailAuthenticator authenticator, IAirbnbEmailMailboxConnectionRepository repository)
    {
        _authenticator = authenticator;
        _repository = repository;
    }

    public async ValueTask<Result<AirbnbEmailMailboxConnectionResult>> Handle(
        ConnectAirbnbEmailMailboxCommand command, CancellationToken cancellationToken)
    {
        var outcome = await _authenticator.ConnectInteractiveAsync(command.TenantId, cancellationToken);

        if (!outcome.IsSuccess)
            return Result.Failure<AirbnbEmailMailboxConnectionResult>(ToError(outcome.FailureReason!.Value));

        var connection = await _repository.GetForCurrentTenantAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Airbnb Email Bridge connection row for tenant {command.TenantId:D} disappeared after a successful authentication.");

        return Result.Success(ToResult(connection));
    }

    private static Error ToError(AirbnbEmailAuthenticationFailureReason reason)
    {
        var code = reason switch
        {
            AirbnbEmailAuthenticationFailureReason.UserCancelled => AirbnbEmailBridgeErrorCodes.UserCancelled,
            AirbnbEmailAuthenticationFailureReason.ConsentDenied => AirbnbEmailBridgeErrorCodes.ConsentDenied,
            AirbnbEmailAuthenticationFailureReason.AccountIdentityUnavailable => AirbnbEmailBridgeErrorCodes.AccountIdentityUnavailable,
            _ => AirbnbEmailBridgeErrorCodes.AcquisitionFailed,
        };
        return new Error(code, code);
    }

    internal static AirbnbEmailMailboxConnectionResult ToResult(Domain.AirbnbEmailMailboxConnection connection) => new(
        connection.TenantId,
        connection.MailboxAddress,
        connection.AuthorizationStatus,
        connection.IsEnabled,
        connection.LastAuthenticatedAtUtc,
        connection.CreatedAtUtc,
        connection.UpdatedAtUtc);
}
