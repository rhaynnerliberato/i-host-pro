using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

internal sealed class FakeAirbnbEmailWebOAuthAuthenticator : IAirbnbEmailWebOAuthAuthenticator
{
    private readonly AirbnbEmailWebOAuthAuthorizationRequest? _requestToReturn;
    private readonly AirbnbEmailWebOAuthCallbackOutcome _completeOutcome;

    public (Guid TenantId, string AuthorizationCode, byte[] ProtectedPkceVerifier)? LastCompleteCall { get; private set; }

    private FakeAirbnbEmailWebOAuthAuthenticator(
        AirbnbEmailWebOAuthAuthorizationRequest? requestToReturn, AirbnbEmailWebOAuthCallbackOutcome completeOutcome)
    {
        _requestToReturn = requestToReturn;
        _completeOutcome = completeOutcome;
    }

    public static FakeAirbnbEmailWebOAuthAuthenticator NotConfigured() => new(null, AirbnbEmailWebOAuthCallbackOutcome.Success());

    public static FakeAirbnbEmailWebOAuthAuthenticator ReturningRequest(AirbnbEmailWebOAuthAuthorizationRequest request) =>
        new(request, AirbnbEmailWebOAuthCallbackOutcome.Success());

    public static FakeAirbnbEmailWebOAuthAuthenticator CompletingWith(AirbnbEmailWebOAuthCallbackOutcome outcome) =>
        new(null, outcome);

    public AirbnbEmailWebOAuthAuthorizationRequest? BuildAuthorizationRequest() => _requestToReturn;

    public Task<AirbnbEmailWebOAuthCallbackOutcome> CompleteAsync(
        Guid tenantId, string authorizationCode, byte[] protectedPkceVerifier, CancellationToken cancellationToken)
    {
        LastCompleteCall = (tenantId, authorizationCode, protectedPkceVerifier);
        return Task.FromResult(_completeOutcome);
    }
}
