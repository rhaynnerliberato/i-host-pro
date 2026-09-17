using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Airbnb Email Bridge Minimal Operations/UX gate.</summary>
public class GetAirbnbEmailBridgeStatusQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Returns_NotConfigured_when_no_connection_exists()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var handler = new GetAirbnbEmailBridgeStatusQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbEmailBridgeStatusQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AirbnbEmailConnectionStatus.NotConfigured);
        result.Value.IsEnabled.Should().BeFalse();
        result.Value.LastAuthenticatedAtUtc.Should().BeNull();
        result.Value.MailboxAddress.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Connected_with_operational_fields_when_connection_is_active()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new GetAirbnbEmailBridgeStatusQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbEmailBridgeStatusQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AirbnbEmailConnectionStatus.Connected);
        result.Value.IsEnabled.Should().BeTrue();
        result.Value.LastAuthenticatedAtUtc.Should().Be(Now);
        result.Value.MailboxAddress.Should().Be("guest@hotmail.com");
    }

    [Fact]
    public async Task Returns_Disconnected_when_connection_was_disconnected()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", null, "guest@hotmail.com", "Mail.Read", Now);
        connection.Disconnect(Now);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new GetAirbnbEmailBridgeStatusQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbEmailBridgeStatusQuery(TenantId), CancellationToken.None);

        result.Value.Status.Should().Be(AirbnbEmailConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task Returns_Error_when_connection_is_in_an_error_state()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", null, "guest@hotmail.com", "Mail.Read", Now);
        connection.MarkError(Now);
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var handler = new GetAirbnbEmailBridgeStatusQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbEmailBridgeStatusQuery(TenantId), CancellationToken.None);

        result.Value.Status.Should().Be(AirbnbEmailConnectionStatus.Error);
    }

    [Fact]
    public async Task Never_exposes_HomeAccountId_or_AccountTenantId_or_GrantedScopes()
    {
        var props = typeof(AirbnbEmailBridgeStatusResult).GetProperties().Select(p => p.Name);
        props.Should().NotContain(["HomeAccountId", "AccountTenantId", "GrantedScopes", "TokenCacheBlob"]);
    }
}
