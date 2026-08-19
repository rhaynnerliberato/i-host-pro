using FluentValidation;

namespace IHostPro.Contexts.Configuration.Application.Templates;

public sealed class CreateTemplateCommandValidator : AbstractValidator<CreateTemplateCommand>
{
    public CreateTemplateCommandValidator()
    {
        RuleFor(c => c.Key).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Content).NotEmpty();
    }
}
