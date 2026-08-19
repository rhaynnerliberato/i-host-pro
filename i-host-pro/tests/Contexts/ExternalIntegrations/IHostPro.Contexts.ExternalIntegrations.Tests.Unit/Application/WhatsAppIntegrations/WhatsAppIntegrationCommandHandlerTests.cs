using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;

public class WhatsAppIntegrationCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider FixedTime = new(Now);

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_creates_a_new_integration_disabled_by_default()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, FixedTime);

        var result = await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, "waba-1", "phone-1", "access-ref", "secret-ref", "verify-ref"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.WabaId.Should().Be("waba-1");
        result.Value.PhoneNumberId.Should().Be("phone-1");
        result.Value.IsEnabled.Should().BeFalse("no path in this checkpoint can enable a real integration");
        result.Value.AccessTokenConfigured.Should().BeTrue();
        result.Value.AppSecretConfigured.Should().BeTrue();
        result.Value.VerifyTokenConfigured.Should().BeTrue();
        result.Value.CreatedAtUtc.Should().Be(Now);
        repository.AddedIntegrations.Should().ContainSingle();
    }

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_upserts_the_existing_integration_for_the_tenant()
    {
        var existing = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        existing.UpdateConfiguration("old-waba", "old-phone", "old-access", null, null, Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(existing);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, FixedTime);

        var result = await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, "new-waba", "new-phone", "new-access", "new-secret", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WabaId.Should().Be("new-waba");
        result.Value.PhoneNumberId.Should().Be("new-phone");
        result.Value.AccessTokenConfigured.Should().BeTrue();
        result.Value.AppSecretConfigured.Should().BeTrue();
        result.Value.VerifyTokenConfigured.Should().BeFalse("only the access/app-secret references were supplied this time");
        repository.AddedIntegrations.Should().BeEmpty("an existing row must be updated in place, never re-added");
    }

    [Fact]
    public async Task GetWhatsAppIntegrationQueryHandler_returns_not_configured_when_tenant_has_none_yet()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var handler = new GetWhatsAppIntegrationQueryHandler(repository);

        var result = await handler.Handle(new GetWhatsAppIntegrationQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a tenant with no integration configured yet is a legitimate state, never an error");
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.WabaId.Should().BeNull();
        result.Value.IsEnabled.Should().BeFalse();
        result.Value.AccessTokenConfigured.Should().BeFalse();
        result.Value.AppSecretConfigured.Should().BeFalse();
        result.Value.VerifyTokenConfigured.Should().BeFalse();
        result.Value.CreatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetWhatsAppIntegrationQueryHandler_returns_the_existing_integration_without_secret_values()
    {
        var existing = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        existing.UpdateConfiguration("waba-1", "phone-1", "access-ref", null, "verify-ref", Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(existing);
        var handler = new GetWhatsAppIntegrationQueryHandler(repository);

        var result = await handler.Handle(new GetWhatsAppIntegrationQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WabaId.Should().Be("waba-1");
        result.Value.AccessTokenConfigured.Should().BeTrue();
        result.Value.AppSecretConfigured.Should().BeFalse();
        result.Value.VerifyTokenConfigured.Should().BeTrue();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
