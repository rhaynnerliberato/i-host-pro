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
        integration.IsEnabled.Should().BeFalse("Create must never start an integration already enabled");
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

    // ---- Real Tenant WhatsApp Activation Readiness gate: Enable/Disable ----

    private static WhatsAppIntegration BuildFullyConfiguredIntegration()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        integration.UpdateConfiguration("waba-1", "phone-1", "access-ref", "app-secret-ref", "verify-ref", Now);
        return integration;
    }

    [Fact]
    public void Enable_from_a_fully_configured_state_becomes_enabled()
    {
        var integration = BuildFullyConfiguredIntegration();
        var enabledAt = Now.AddMinutes(1);

        integration.Enable(enabledAt);

        integration.IsEnabled.Should().BeTrue();
        integration.UpdatedAtUtc.Should().Be(enabledAt);
    }

    [Theory]
    [InlineData(null, "phone-1", "access-ref", "app-secret-ref", "verify-ref")]
    [InlineData("waba-1", null, "access-ref", "app-secret-ref", "verify-ref")]
    [InlineData("waba-1", "phone-1", null, "app-secret-ref", "verify-ref")]
    [InlineData("waba-1", "phone-1", "access-ref", null, "verify-ref")]
    [InlineData("waba-1", "phone-1", "access-ref", "app-secret-ref", null)]
    public void Enable_without_every_required_field_throws(
        string? wabaId, string? phoneNumberId, string? accessTokenRef, string? appSecretRef, string? verifyTokenRef)
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        integration.UpdateConfiguration(wabaId, phoneNumberId, accessTokenRef, appSecretRef, verifyTokenRef, Now);

        var act = () => integration.Enable(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
        integration.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Enable_when_already_enabled_throws()
    {
        var integration = BuildFullyConfiguredIntegration();
        integration.Enable(Now.AddMinutes(1));

        var act = () => integration.Enable(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Disable_from_an_enabled_state_becomes_disabled()
    {
        var integration = BuildFullyConfiguredIntegration();
        integration.Enable(Now.AddMinutes(1));
        var disabledAt = Now.AddMinutes(2);

        integration.Disable(disabledAt);

        integration.IsEnabled.Should().BeFalse();
        integration.UpdatedAtUtc.Should().Be(disabledAt);
    }

    [Fact]
    public void Disable_when_already_disabled_throws()
    {
        var integration = BuildFullyConfiguredIntegration();

        var act = () => integration.Disable(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }
}
