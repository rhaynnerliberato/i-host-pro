using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

public class StartAirbnbEmailWebOAuthCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Returns_the_authorization_url_and_persists_the_HASHED_state_never_the_raw_state()
    {
        var request = new AirbnbEmailWebOAuthAuthorizationRequest("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=raw-state-value", "raw-state-value", [1, 2, 3]);
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.ReturningRequest(request);
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        var handler = new StartAirbnbEmailWebOAuthCommandHandler(authenticator, transactionRepository, new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartAirbnbEmailWebOAuthCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AuthorizationUrl.Should().Be(request.AuthorizationUrl);

        transactionRepository.LastCreatedStateHash.Should().Be(AirbnbEmailOAuthStateHasher.Hash("raw-state-value"));
        transactionRepository.LastCreatedStateHash.Should().NotBe("raw-state-value", "the raw state must never be persisted, only its hash");
    }

    [Fact]
    public async Task Persists_a_ten_minute_expiry_window()
    {
        var request = new AirbnbEmailWebOAuthAuthorizationRequest("https://example.test/authorize", "state-1", [9]);
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.ReturningRequest(request);
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        var handler = new StartAirbnbEmailWebOAuthCommandHandler(authenticator, transactionRepository, new FixedTimeProvider(Now));

        await handler.Handle(new StartAirbnbEmailWebOAuthCommand(TenantId, ActorUserId), CancellationToken.None);

        transactionRepository.LastCreatedAtUtc.Should().Be(Now);
        transactionRepository.LastCreatedExpiresAtUtc.Should().Be(Now.AddMinutes(10));
    }

    [Fact]
    public async Task Fails_with_WebOAuthNotConfigured_and_persists_nothing_when_the_authenticator_is_not_configured()
    {
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.NotConfigured();
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        var handler = new StartAirbnbEmailWebOAuthCommandHandler(authenticator, transactionRepository, new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartAirbnbEmailWebOAuthCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured);
        transactionRepository.LastCreatedStateHash.Should().BeNull("nothing should be persisted when authorization request building fails");
    }
}
