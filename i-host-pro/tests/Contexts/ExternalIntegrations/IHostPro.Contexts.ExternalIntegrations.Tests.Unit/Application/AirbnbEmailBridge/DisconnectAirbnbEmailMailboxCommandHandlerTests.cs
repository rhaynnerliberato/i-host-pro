using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

public class DisconnectAirbnbEmailMailboxCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();

    [Fact]
    public async Task Returns_not_found_when_the_tenant_has_no_connection()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new DisconnectAirbnbEmailMailboxCommandHandler(repository, new FakeAirbnbEmailTokenCacheStore(repository));

        var result = await handler.Handle(new DisconnectAirbnbEmailMailboxCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ConnectionNotFound);
    }

    [Fact]
    public async Task Disconnects_via_the_token_cache_store_and_returns_the_disabled_state()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, DateTimeOffset.UtcNow);
        connection.Connect("home-account-1", null, "guest@hotmail.com", "Mail.Read", DateTimeOffset.UtcNow);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var tokenCacheStore = new FakeAirbnbEmailTokenCacheStore(repository);
        var handler = new DisconnectAirbnbEmailMailboxCommandHandler(repository, tokenCacheStore);

        var result = await handler.Handle(new DisconnectAirbnbEmailMailboxCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        tokenCacheStore.ClearWasCalled.Should().BeTrue();
        result.Value.IsEnabled.Should().BeFalse();
        result.Value.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Disconnected);
    }
}
