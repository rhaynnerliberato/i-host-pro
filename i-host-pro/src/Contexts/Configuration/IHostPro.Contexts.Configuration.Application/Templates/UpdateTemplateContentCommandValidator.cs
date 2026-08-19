using FluentValidation;

namespace IHostPro.Contexts.Configuration.Application.Templates;

public sealed class UpdateTemplateContentCommandValidator : AbstractValidator<UpdateTemplateContentCommand>
{
    public UpdateTemplateContentCommandValidator()
    {
        RuleFor(c => c.Key).NotEmpty();
        RuleFor(c => c.Content).NotEmpty();
    }
}
