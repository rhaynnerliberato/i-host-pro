using FluentValidation;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

public sealed class ConfigureWhatsAppTemplateMappingCommandValidator : AbstractValidator<ConfigureWhatsAppTemplateMappingCommand>
{
    public ConfigureWhatsAppTemplateMappingCommandValidator()
    {
        RuleFor(c => c.TemplateKey).NotEmpty().MaximumLength(100);
        RuleFor(c => c.ProviderTemplateName).NotEmpty().MaximumLength(512);
        RuleFor(c => c.LanguageCode).NotEmpty().MaximumLength(20);
        RuleForEach(c => c.ParameterOrder).NotEmpty().MaximumLength(100);
    }
}
