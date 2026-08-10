using FluentValidation;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>Request-shape validation only — catalog-shape validation of <see cref="CreatePolicyValueVersionCommand.Value"/> is <see cref="PolicyValueValidation"/>'s job, run separately by the handler.</summary>
public sealed class CreatePolicyValueVersionCommandValidator : AbstractValidator<CreatePolicyValueVersionCommand>
{
    public CreatePolicyValueVersionCommandValidator()
    {
        RuleFor(c => c.PolicyCode).NotEmpty();
        RuleFor(c => c.ScopeType).NotEmpty();
        RuleFor(c => c.Value).NotEmpty();
        RuleFor(c => c.Reason).NotEmpty();
    }
}
