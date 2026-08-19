using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

public sealed class ConfigureWhatsAppIntegrationCommandHandler
    : ICommandHandler<ConfigureWhatsAppIntegrationCommand, WhatsAppIntegrationResult>
{
    private readonly IWhatsAppIntegrationRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ConfigureWhatsAppIntegrationCommandHandler(IWhatsAppIntegrationRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
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

        return Result.Success(ToResult(integration));
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
