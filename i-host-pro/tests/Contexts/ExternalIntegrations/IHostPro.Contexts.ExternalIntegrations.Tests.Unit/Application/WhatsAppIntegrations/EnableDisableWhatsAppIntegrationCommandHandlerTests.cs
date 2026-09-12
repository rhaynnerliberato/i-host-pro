using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;

/// <summary>
/// Real Tenant WhatsApp Activation Readiness gate (SMALL_IMPLEMENTATION_GAP
/// plan) — focused tests for the new explicit Enable/Disable commands.
/// Mirrors the surrounding <c>WhatsAppIntegrationCommandHandlerTests</c>'
/// own fixture style exactly.
/// </summary>
public class EnableDisableWhatsAppIntegrationCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider FixedTime = new(Now);

    private static WhatsAppIntegration BuildFullyConfiguredIntegration()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        integration.UpdateConfiguration("waba-1", "phone-1", "access-ref", "app-secret-ref", "verify-ref", Now);
        return integration;
    }

    // ---- Enable ----------------------------------------------------------

    [Fact]
    public async Task Enable_from_a_valid_fully_configured_state_becomes_enabled()
    {
        var integration = BuildFullyConfiguredIntegration();
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(integration);
        var handler = new EnableWhatsAppIntegrationCommandHandler(repository, FakeWhatsAppCredentialProvider.Returning("resolved-secret"), FixedTime);

        var result = await handler.Handle(new EnableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEnabled.Should().BeTrue();
        integration.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enable_when_no_integration_exists_yet_is_rejected_as_not_found()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var handler = new EnableWhatsAppIntegrationCommandHandler(repository, FakeWhatsAppCredentialProvider.Returning("resolved-secret"), FixedTime);

        var result = await handler.Handle(new EnableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound);
    }

    [Fact]
    public async Task Enable_when_already_enabled_is_rejected_without_re_enabling()
    {
        var integration = BuildFullyConfiguredIntegration();
        integration.Enable(Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(integration);
        var handler = new EnableWhatsAppIntegrationCommandHandler(repository, FakeWhatsAppCredentialProvider.Returning("resolved-secret"), FixedTime);

        var result = await handler.Handle(new EnableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyEnabled);
    }

    [Fact]
    public async Task Enable_without_required_configuration_is_rejected_without_calling_the_credential_provider()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        integration.UpdateConfiguration("waba-1", "phone-1", null, null, null, Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(integration);
        var credentialProvider = FakeWhatsAppCredentialProvider.Returning("resolved-secret");
        var handler = new EnableWhatsAppIntegrationCommandHandler(repository, credentialProvider, FixedTime);

        var result = await handler.Handle(new EnableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(WhatsAppIntegrationErrorCodes.WhatsAppIntegrationConfigurationIncomplete);
        integration.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Enable_when_a_configured_secret_reference_cannot_be_resolved_is_rejected_as_credentials_unavailable()
    {
        var integration = BuildFullyConfiguredIntegration();
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(integration);
        var handler = new EnableWhatsAppIntegrationCommandHandler(repository, FakeWhatsAppCredentialProvider.Returning(null), FixedTime);

        var result = await handler.Handle(new EnableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(WhatsAppIntegrationErrorCodes.WhatsAppIntegrationCredentialsUnavailable);
        integration.IsEnabled.Should().BeFalse("a reference existing is not proof the real value is actually resolvable");
    }

    // ---- Disable -----------------------------------------------------------

    [Fact]
    public async Task Disable_from_an_enabled_state_becomes_disabled()
    {
        var integration = BuildFullyConfiguredIntegration();
        integration.Enable(Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(integration);
        var handler = new DisableWhatsAppIntegrationCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new DisableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEnabled.Should().BeFalse();
        integration.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Disable_when_no_integration_exists_yet_is_rejected_as_not_found()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var handler = new DisableWhatsAppIntegrationCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new DisableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound);
    }

    [Fact]
    public async Task Disable_when_already_disabled_is_rejected()
    {
        var integration = BuildFullyConfiguredIntegration();
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(integration);
        var handler = new DisableWhatsAppIntegrationCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new DisableWhatsAppIntegrationCommand(TenantId, ActorUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyDisabled);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
