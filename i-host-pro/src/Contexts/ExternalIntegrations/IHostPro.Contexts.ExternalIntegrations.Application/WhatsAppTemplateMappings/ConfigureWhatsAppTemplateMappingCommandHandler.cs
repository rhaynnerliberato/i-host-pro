using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

public sealed class ConfigureWhatsAppTemplateMappingCommandHandler
    : ICommandHandler<ConfigureWhatsAppTemplateMappingCommand, WhatsAppTemplateMappingResult>
{
    private readonly IWhatsAppTemplateMappingRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ConfigureWhatsAppTemplateMappingCommandHandler(IWhatsAppTemplateMappingRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<WhatsAppTemplateMappingResult>> Handle(
        ConfigureWhatsAppTemplateMappingCommand command, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var mapping = await _repository.GetForCurrentTenantByTemplateKeyAsync(command.TemplateKey, cancellationToken);

        if (mapping is null)
        {
            mapping = Domain.WhatsAppTemplateMapping.Create(
                Guid.NewGuid(), command.TenantId, command.TemplateKey,
                command.ProviderTemplateName, command.LanguageCode, command.ParameterOrder, now);
            _repository.Add(mapping);
        }
        else
        {
            mapping.UpdateMapping(command.ProviderTemplateName, command.LanguageCode, command.ParameterOrder, now);
        }

        return Result.Success(ToResult(mapping));
    }

    internal static WhatsAppTemplateMappingResult ToResult(Domain.WhatsAppTemplateMapping mapping) => new(
        mapping.TenantId, mapping.TemplateKey, mapping.ProviderTemplateName, mapping.LanguageCode,
        mapping.ParameterOrder, mapping.CreatedAtUtc, mapping.UpdatedAtUtc);
}
