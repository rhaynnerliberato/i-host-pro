using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Automatic Publication Design + Safety gate.</summary>
public class AirbnbAutoPublicationCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static AirbnbEmailMailboxConnection ConnectedConnection()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", null, "guest@hotmail.com", "Mail.Read", Now);
        return connection;
    }

    // ---- Enable ----

    [Fact]
    public async Task Enable_requires_an_explicit_cutoff()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(ConnectedConnection());
        var handler = new EnableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(
            new EnableAirbnbAutoPublicationCommand(TenantId, ActorUserId, NotBeforeUtc: null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.AutoPublicationCutoffRequired);
    }

    [Fact]
    public async Task Enable_fails_when_no_connection_exists()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new EnableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(
            new EnableAirbnbAutoPublicationCommand(TenantId, ActorUserId, Now), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ConnectionNotFound);
    }

    [Fact]
    public async Task Enable_fails_when_the_mailbox_is_not_connected()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now); // never Connect()'d
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new EnableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(
            new EnableAirbnbAutoPublicationCommand(TenantId, ActorUserId, Now), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.AutoPublicationMailboxNotConnected);
    }

    [Fact]
    public async Task Enable_rejects_a_second_enable_while_already_enabled()
    {
        var connection = ConnectedConnection();
        connection.EnableAutoPublish(Now, Now);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new EnableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(
            new EnableAirbnbAutoPublicationCommand(TenantId, ActorUserId, Now.AddDays(1)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyEnabled);
    }

    [Fact]
    public async Task Enable_succeeds_and_persists_the_exact_cutoff_given()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(ConnectedConnection());
        var handler = new EnableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);
        var cutoff = Now.AddMinutes(5);

        var result = await handler.Handle(
            new EnableAirbnbAutoPublicationCommand(TenantId, ActorUserId, cutoff), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AutoPublishEnabled.Should().BeTrue();
        result.Value.AutoPublishNotBeforeUtc.Should().Be(cutoff);
    }

    // ---- Disable ----

    [Fact]
    public async Task Disable_fails_when_no_connection_exists()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new DisableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(new DisableAirbnbAutoPublicationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ConnectionNotFound);
    }

    [Fact]
    public async Task Disable_rejects_a_connection_that_is_not_currently_enabled()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(ConnectedConnection());
        var handler = new DisableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(new DisableAirbnbAutoPublicationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyDisabled);
    }

    [Fact]
    public async Task Disable_succeeds_and_preserves_the_mailbox_connection()
    {
        var connection = ConnectedConnection();
        connection.EnableAutoPublish(Now, Now);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new DisableAirbnbAutoPublicationCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(new DisableAirbnbAutoPublicationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AutoPublishEnabled.Should().BeFalse();
        result.Value.AutoPublishNotBeforeUtc.Should().BeNull();
        connection.IsEnabled.Should().BeTrue("disabling auto-publication must never disconnect the mailbox itself");
        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Connected);
    }

    // ---- Get status ----

    [Fact]
    public async Task GetStatus_reports_not_configured_when_no_connection_exists_yet()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new GetAirbnbAutoPublicationStatusQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbAutoPublicationStatusQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AutoPublishEnabled.Should().BeFalse();
        result.Value.AutoPublishNotBeforeUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_reflects_the_current_connection_state()
    {
        var connection = ConnectedConnection();
        var cutoff = Now.AddDays(-1);
        connection.EnableAutoPublish(cutoff, Now);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new GetAirbnbAutoPublicationStatusQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbAutoPublicationStatusQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AutoPublishEnabled.Should().BeTrue();
        result.Value.AutoPublishNotBeforeUtc.Should().Be(cutoff);
    }
}
