using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class WhatsAppIntegrationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_disabled_and_unconfigured()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);

        integration.TenantId.Should().Be(TenantId);
        integration.IsEnabled.Should().BeFalse("no path in this checkpoint can enable a real integration (CP2.1 mandate §18)");
        integration.WabaId.Should().BeNull();
        integration.PhoneNumberId.Should().BeNull();
        integration.AccessTokenSecretReference.Should().BeNull();
        integration.AppSecretSecretReference.Should().BeNull();
        integration.VerifyTokenSecretReference.Should().BeNull();
        integration.CreatedAtUtc.Should().Be(Now);
        integration.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void UpdateConfiguration_sets_non_secret_identifiers_and_secret_references()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        var updatedAt = Now.AddMinutes(5);

        integration.UpdateConfiguration("waba-1", "phone-1", "access-ref", "app-secret-ref", "verify-ref", updatedAt);

        integration.WabaId.Should().Be("waba-1");
        integration.PhoneNumberId.Should().Be("phone-1");
        integration.AccessTokenSecretReference.Should().Be("access-ref");
        integration.AppSecretSecretReference.Should().Be("app-secret-ref");
        integration.VerifyTokenSecretReference.Should().Be("verify-ref");
        integration.UpdatedAtUtc.Should().Be(updatedAt);
    }

    [Fact]
    public void UpdateConfiguration_never_changes_IsEnabled()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);

        integration.UpdateConfiguration("waba-1", "phone-1", "access-ref", "app-secret-ref", "verify-ref", Now);

        integration.IsEnabled.Should().BeFalse("UpdateConfiguration must never be a hidden path to enabling a real integration");
    }

    [Fact]
    public void UpdateConfiguration_can_clear_a_previously_set_reference()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        integration.UpdateConfiguration("waba-1", "phone-1", "access-ref", "app-secret-ref", "verify-ref", Now);

        integration.UpdateConfiguration("waba-1", "phone-1", "access-ref", null, null, Now.AddMinutes(1));

        integration.AppSecretSecretReference.Should().BeNull();
        integration.VerifyTokenSecretReference.Should().BeNull();
        integration.AccessTokenSecretReference.Should().Be("access-ref");
    }
}
