using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;

public class WhatsAppIntegrationCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider FixedTime = new(Now);

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_creates_a_new_integration_disabled_by_default()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(null);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        var result = await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "waba-1", "phone-1", "access-ref", "secret-ref", "verify-ref"),
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
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(
            WhatsAppTenantRoute.Create(Guid.NewGuid(), "old-phone", TenantId, Now));
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        var result = await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "new-waba", "new-phone", "new-access", "new-secret", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WabaId.Should().Be("new-waba");
        result.Value.PhoneNumberId.Should().Be("new-phone");
        result.Value.AccessTokenConfigured.Should().BeTrue();
        result.Value.AppSecretConfigured.Should().BeTrue();
        result.Value.VerifyTokenConfigured.Should().BeFalse("only the access/app-secret references were supplied this time");
        repository.AddedIntegrations.Should().BeEmpty("an existing row must be updated in place, never re-added");
    }

    // ---- Fase 9, Checkpoint 2.3.2: tenant route synchronization ----------------

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_creates_a_route_for_a_brand_new_integration()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(null);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "waba-1", "phone-1", "access-ref", null, null),
            CancellationToken.None);

        routeRepository.AddedRoutes.Should().ContainSingle();
        routeRepository.AddedRoutes[0].PhoneNumberId.Should().Be("phone-1");
        routeRepository.AddedRoutes[0].TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_updates_the_route_when_PhoneNumberId_changes()
    {
        var existing = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        existing.UpdateConfiguration("waba-1", "old-phone", "access-ref", null, null, Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(existing);
        var existingRoute = WhatsAppTenantRoute.Create(Guid.NewGuid(), "old-phone", TenantId, Now);
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(existingRoute);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "waba-1", "new-phone", "access-ref", null, null),
            CancellationToken.None);

        routeRepository.AddedRoutes.Should().BeEmpty("an existing route must be updated in place, never re-added");
        routeRepository.Current!.PhoneNumberId.Should().Be("new-phone");
    }

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_does_not_touch_the_route_when_PhoneNumberId_is_unchanged()
    {
        var existing = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        existing.UpdateConfiguration("waba-1", "same-phone", "access-ref", null, null, Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(existing);
        var existingRoute = WhatsAppTenantRoute.Create(Guid.NewGuid(), "same-phone", TenantId, Now);
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(existingRoute);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "waba-1", "same-phone", "access-ref", null, null),
            CancellationToken.None);

        routeRepository.AddedRoutes.Should().BeEmpty();
        routeRepository.RemovedRoutes.Should().BeEmpty();
        routeRepository.Current!.UpdatedAtUtc.Should().BeNull("nothing changed, so the route must not be touched at all");
    }

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_removes_the_route_when_PhoneNumberId_is_cleared()
    {
        var existing = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, Now);
        existing.UpdateConfiguration("waba-1", "old-phone", "access-ref", null, null, Now);
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(existing);
        var existingRoute = WhatsAppTenantRoute.Create(Guid.NewGuid(), "old-phone", TenantId, Now);
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(existingRoute);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "waba-1", null, "access-ref", null, null),
            CancellationToken.None);

        routeRepository.RemovedRoutes.Should().ContainSingle(
            "a stale route must never linger and resolve a phone number this tenant no longer claims");
    }

    [Fact]
    public async Task ConfigureWhatsAppIntegrationCommandHandler_does_not_create_a_route_when_no_PhoneNumberId_is_ever_configured()
    {
        var repository = FakeWhatsAppIntegrationRepository.WithExisting(null);
        var routeRepository = FakeWhatsAppTenantRouteRepository.WithExisting(null);
        var handler = new ConfigureWhatsAppIntegrationCommandHandler(repository, routeRepository, FixedTime);

        await handler.Handle(
            new ConfigureWhatsAppIntegrationCommand(TenantId, ActorUserId, "waba-1", null, "access-ref", null, null),
            CancellationToken.None);

        routeRepository.AddedRoutes.Should().BeEmpty();
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
