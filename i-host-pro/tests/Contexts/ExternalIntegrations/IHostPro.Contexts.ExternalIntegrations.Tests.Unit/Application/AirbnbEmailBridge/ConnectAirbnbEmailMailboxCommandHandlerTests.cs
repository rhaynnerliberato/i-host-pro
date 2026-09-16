using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

public class ConnectAirbnbEmailMailboxCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();

    [Fact]
    public async Task Successful_authentication_returns_the_connected_state()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new ConnectAirbnbEmailMailboxCommandHandler(
            FakeAirbnbEmailAuthenticator.Succeeding(repository), repository, new FakeAirbnbEmailUnitOfWork());

        var result = await handler.Handle(new ConnectAirbnbEmailMailboxCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.MailboxAddress.Should().Be("guest@hotmail.com");
        result.Value.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Connected);
        result.Value.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(AirbnbEmailAuthenticationFailureReason.UserCancelled, AirbnbEmailBridgeErrorCodes.UserCancelled)]
    [InlineData(AirbnbEmailAuthenticationFailureReason.ConsentDenied, AirbnbEmailBridgeErrorCodes.ConsentDenied)]
    [InlineData(AirbnbEmailAuthenticationFailureReason.AcquisitionFailed, AirbnbEmailBridgeErrorCodes.AcquisitionFailed)]
    [InlineData(AirbnbEmailAuthenticationFailureReason.AccountIdentityUnavailable, AirbnbEmailBridgeErrorCodes.AccountIdentityUnavailable)]
    public async Task Authentication_failure_maps_to_the_matching_error_code(
        AirbnbEmailAuthenticationFailureReason reason, string expectedErrorCode)
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new ConnectAirbnbEmailMailboxCommandHandler(
            FakeAirbnbEmailAuthenticator.Failing(repository, reason), repository, new FakeAirbnbEmailUnitOfWork());

        var result = await handler.Handle(new ConnectAirbnbEmailMailboxCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedErrorCode);
    }

    [Fact]
    public async Task A_failed_authentication_never_reports_a_connected_state()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(
            AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, DateTimeOffset.UtcNow));
        var handler = new ConnectAirbnbEmailMailboxCommandHandler(
            FakeAirbnbEmailAuthenticator.Failing(repository, AirbnbEmailAuthenticationFailureReason.UserCancelled), repository,
            new FakeAirbnbEmailUnitOfWork());

        var result = await handler.Handle(new ConnectAirbnbEmailMailboxCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        repository.Current!.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Disconnected);
    }
}
