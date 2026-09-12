using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// <c>IsEnabled</c> <see langword="true"/> → <see langword="false"/>. Same
/// caller-responsibility split as <see cref="EnableWhatsAppIntegrationCommandHandler"/>
/// — no credential preflight needed here, since disabling never depends on
/// any external resource resolving successfully.
/// </summary>
public sealed class DisableWhatsAppIntegrationCommandHandler : ICommandHandler<DisableWhatsAppIntegrationCommand, WhatsAppIntegrationResult>
{
    private static readonly Error NotFoundError = new(
        WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound);
    private static readonly Error AlreadyDisabledError = new(
        WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyDisabled, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyDisabled);

    private readonly IWhatsAppIntegrationRepository _repository;
    private readonly TimeProvider _timeProvider;

    public DisableWhatsAppIntegrationCommandHandler(IWhatsAppIntegrationRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(DisableWhatsAppIntegrationCommand command, CancellationToken cancellationToken)
    {
        var integration = await _repository.GetForCurrentTenantAsync(cancellationToken);
        if (integration is null)
            return Result.Failure<WhatsAppIntegrationResult>(NotFoundError);

        if (!integration.IsEnabled)
            return Result.Failure<WhatsAppIntegrationResult>(AlreadyDisabledError);

        integration.Disable(_timeProvider.GetUtcNow());

        return Result.Success(ConfigureWhatsAppIntegrationCommandHandler.ToResult(integration));
    }
}
