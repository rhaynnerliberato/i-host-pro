using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>State lifetime — see the Web OAuth architecture gate's explicit 10-minute decision.</summary>
public sealed class StartAirbnbEmailWebOAuthCommandHandler : ICommandHandler<StartAirbnbEmailWebOAuthCommand, AirbnbEmailWebOAuthStartResult>
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly Error NotConfiguredError = new(AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured, AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured);

    private readonly IAirbnbEmailWebOAuthAuthenticator _authenticator;
    private readonly IAirbnbEmailOAuthTransactionRepository _transactionRepository;
    private readonly TimeProvider _timeProvider;

    public StartAirbnbEmailWebOAuthCommandHandler(
        IAirbnbEmailWebOAuthAuthenticator authenticator,
        IAirbnbEmailOAuthTransactionRepository transactionRepository,
        TimeProvider timeProvider)
    {
        _authenticator = authenticator;
        _transactionRepository = transactionRepository;
        _timeProvider = timeProvider;
    }

    public ValueTask<Result<AirbnbEmailWebOAuthStartResult>> Handle(StartAirbnbEmailWebOAuthCommand command, CancellationToken cancellationToken)
    {
        var request = _authenticator.BuildAuthorizationRequest();
        if (request is null)
            return new ValueTask<Result<AirbnbEmailWebOAuthStartResult>>(Result.Failure<AirbnbEmailWebOAuthStartResult>(NotConfiguredError));

        var now = _timeProvider.GetUtcNow();
        _transactionRepository.CreatePending(
            Guid.NewGuid(), command.TenantId, command.ActorUserId,
            AirbnbEmailOAuthStateHasher.Hash(request.State), request.ProtectedPkceVerifier,
            now, now.Add(StateLifetime));

        return new ValueTask<Result<AirbnbEmailWebOAuthStartResult>>(
            Result.Success(new AirbnbEmailWebOAuthStartResult(request.AuthorizationUrl)));
    }
}
