using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class AirbnbIntegrationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_disabled_and_unconfigured()
    {
        var integration = AirbnbIntegration.Create(Guid.NewGuid(), TenantId, Now);

        integration.TenantId.Should().Be(TenantId);
        integration.IsEnabled.Should().BeFalse("no real Airbnb connector or partner credentials exist yet");
        integration.ExternalAccountId.Should().BeNull();
        integration.CredentialSecretReference.Should().BeNull();
        integration.CreatedAtUtc.Should().Be(Now);
        integration.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void UpdateConfiguration_sets_external_account_id_and_secret_reference()
    {
        var integration = AirbnbIntegration.Create(Guid.NewGuid(), TenantId, Now);
        var updatedAt = Now.AddMinutes(5);

        integration.UpdateConfiguration("airbnb-account-1", "credential-ref", updatedAt);

        integration.ExternalAccountId.Should().Be("airbnb-account-1");
        integration.CredentialSecretReference.Should().Be("credential-ref");
        integration.UpdatedAtUtc.Should().Be(updatedAt);
    }

    [Fact]
    public void UpdateConfiguration_never_changes_IsEnabled()
    {
        var integration = AirbnbIntegration.Create(Guid.NewGuid(), TenantId, Now);

        integration.UpdateConfiguration("airbnb-account-1", "credential-ref", Now);

        integration.IsEnabled.Should().BeFalse("UpdateConfiguration must never be a hidden path to enabling a real integration");
    }
}
