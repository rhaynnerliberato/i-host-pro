using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class AirbnbEmailMailboxConnectionTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_disconnected_and_disabled()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);

        connection.TenantId.Should().Be(TenantId);
        connection.IsEnabled.Should().BeFalse();
        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Disconnected);
        connection.HomeAccountId.Should().BeNull();
        connection.TokenCacheBlob.Should().BeNull();
    }

    [Fact]
    public void Connect_stores_the_stable_account_identity_and_marks_connected()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);

        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);

        connection.HomeAccountId.Should().Be("home-account-1");
        connection.AccountTenantId.Should().Be("entra-tenant-1");
        connection.MailboxAddress.Should().Be("guest@hotmail.com");
        connection.GrantedScopes.Should().Be("Mail.Read");
        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Connected);
        connection.IsEnabled.Should().BeTrue();
        connection.LastAuthenticatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Connect_never_touches_the_token_cache_blob()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);

        connection.Connect("home-account-1", null, null, "Mail.Read", Now);

        connection.TokenCacheBlob.Should().BeNull("persisting the cache is a separate concern handled by UpdateTokenCache");
    }

    [Fact]
    public void Connect_rejects_an_empty_home_account_id()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);

        var act = () => connection.Connect("", null, null, null, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateTokenCache_replaces_the_blob_without_touching_the_account_identity()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", null, null, "Mail.Read", Now);
        connection.UpdateTokenCache([1], Now);
        var refreshedAt = Now.AddMinutes(30);

        connection.UpdateTokenCache([9, 9, 9], refreshedAt);

        connection.TokenCacheBlob.Should().Equal(9, 9, 9);
        connection.HomeAccountId.Should().Be("home-account-1", "a silent token refresh must never change the account identity");
        connection.UpdatedAtUtc.Should().Be(refreshedAt);
    }

    [Fact]
    public void MarkError_flags_the_connection_without_clearing_the_cache()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", null, null, "Mail.Read", Now);
        connection.UpdateTokenCache([1], Now);

        connection.MarkError(Now.AddHours(1));

        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Error);
        connection.TokenCacheBlob.Should().NotBeNull("diagnostics may still need the last known cache");
    }

    [Fact]
    public void Disconnect_clears_the_cache_and_disables_the_connection_but_keeps_the_account_identity()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);
        connection.UpdateTokenCache([1, 2, 3], Now);

        connection.Disconnect(Now.AddDays(1));

        connection.TokenCacheBlob.Should().BeNull();
        connection.GrantedScopes.Should().BeNull();
        connection.IsEnabled.Should().BeFalse();
        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Disconnected);
        connection.HomeAccountId.Should().Be("home-account-1", "historical account identity is retained for a future reconnect");
        connection.MailboxAddress.Should().Be("guest@hotmail.com");
    }
}
