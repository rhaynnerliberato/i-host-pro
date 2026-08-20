using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

public sealed class ConfigureWhatsAppIntegrationCommandHandler
    : ICommandHandler<ConfigureWhatsAppIntegrationCommand, WhatsAppIntegrationResult>
{
    private readonly IWhatsAppIntegrationRepository _repository;
    private readonly IWhatsAppTenantRouteRepository _routeRepository;
    private readonly TimeProvider _timeProvider;

    public ConfigureWhatsAppIntegrationCommandHandler(
        IWhatsAppIntegrationRepository repository,
        IWhatsAppTenantRouteRepository routeRepository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _routeRepository = routeRepository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(
        ConfigureWhatsAppIntegrationCommand command, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var integration = await _repository.GetForCurrentTenantAsync(cancellationToken);

        if (integration is null)
        {
            integration = Domain.WhatsAppIntegration.Create(Guid.NewGuid(), command.TenantId, now);
            _repository.Add(integration);
        }

        integration.UpdateConfiguration(
            command.WabaId,
            command.PhoneNumberId,
            command.AccessTokenSecretReference,
            command.AppSecretSecretReference,
            command.VerifyTokenSecretReference,
            now);

        // Fase 9, Checkpoint 2.3.2 (ADR-022 item 9/10): the global routing
        // directory is synchronized here, on the SAME ExternalIntegrationsDbContext
        // instance this repository shares with IWhatsAppIntegrationRepository —
        // TenantTransactionBehavior's single SaveChangesAsync call at the end
        // of this request commits both writes atomically, with no separate
        // transaction-coordination code needed.
        await SyncTenantRouteAsync(command.TenantId, command.PhoneNumberId, now, cancellationToken);

        return Result.Success(ToResult(integration));
    }

    private async Task SyncTenantRouteAsync(
        Guid tenantId, string? phoneNumberId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existingRoute = await _routeRepository.GetByTenantIdAsync(tenantId, cancellationToken);

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            // PhoneNumberId cleared — the old route must not linger and
            // resolve a phone number this tenant no longer claims.
            if (existingRoute is not null)
                _routeRepository.Remove(existingRoute);
            return;
        }

        if (existingRoute is null)
            _routeRepository.Add(Domain.WhatsAppTenantRoute.Create(Guid.NewGuid(), phoneNumberId, tenantId, now));
        else if (existingRoute.PhoneNumberId != phoneNumberId)
            existingRoute.UpdatePhoneNumberId(phoneNumberId, now);
    }

    internal static WhatsAppIntegrationResult ToResult(Domain.WhatsAppIntegration integration) => new(
        integration.TenantId,
        integration.WabaId,
        integration.PhoneNumberId,
        integration.IsEnabled,
        AccessTokenConfigured: integration.AccessTokenSecretReference is not null,
        AppSecretConfigured: integration.AppSecretSecretReference is not null,
        VerifyTokenConfigured: integration.VerifyTokenSecretReference is not null,
        integration.CreatedAtUtc,
        integration.UpdatedAtUtc);
}
