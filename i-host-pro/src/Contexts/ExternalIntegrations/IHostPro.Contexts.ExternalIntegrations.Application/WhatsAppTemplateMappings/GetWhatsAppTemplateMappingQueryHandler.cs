using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

public sealed class GetWhatsAppTemplateMappingQueryHandler
    : IQueryHandler<GetWhatsAppTemplateMappingQuery, WhatsAppTemplateMappingResult>
{
    private readonly IWhatsAppTemplateMappingRepository _repository;

    public GetWhatsAppTemplateMappingQueryHandler(IWhatsAppTemplateMappingRepository repository) => _repository = repository;

    public async ValueTask<Result<WhatsAppTemplateMappingResult>> Handle(
        GetWhatsAppTemplateMappingQuery query, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetForCurrentTenantByTemplateKeyAsync(query.TemplateKey, cancellationToken);

        return Result.Success(mapping is null
            ? WhatsAppTemplateMappingResult.NotConfigured(query.TenantId, query.TemplateKey)
            : ConfigureWhatsAppTemplateMappingCommandHandler.ToResult(mapping));
    }
}
