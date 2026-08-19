using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

public sealed class GetWhatsAppIntegrationQueryHandler : IQueryHandler<GetWhatsAppIntegrationQuery, WhatsAppIntegrationResult>
{
    private readonly IWhatsAppIntegrationRepository _repository;

    public GetWhatsAppIntegrationQueryHandler(IWhatsAppIntegrationRepository repository) => _repository = repository;

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(
        GetWhatsAppIntegrationQuery query, CancellationToken cancellationToken)
    {
        var integration = await _repository.GetForCurrentTenantAsync(cancellationToken);

        return Result.Success(integration is null
            ? WhatsAppIntegrationResult.NotConfigured(query.TenantId)
            : ConfigureWhatsAppIntegrationCommandHandler.ToResult(integration));
    }
}
