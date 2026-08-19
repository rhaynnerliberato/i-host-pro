using FluentValidation;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

public sealed class ConfigureWhatsAppIntegrationCommandValidator : AbstractValidator<ConfigureWhatsAppIntegrationCommand>
{
    public ConfigureWhatsAppIntegrationCommandValidator()
    {
        RuleFor(c => c.WabaId).MaximumLength(100);
        RuleFor(c => c.PhoneNumberId).MaximumLength(100);
        RuleFor(c => c.AccessTokenSecretReference).MaximumLength(200);
        RuleFor(c => c.AppSecretSecretReference).MaximumLength(200);
        RuleFor(c => c.VerifyTokenSecretReference).MaximumLength(200);
    }
}
