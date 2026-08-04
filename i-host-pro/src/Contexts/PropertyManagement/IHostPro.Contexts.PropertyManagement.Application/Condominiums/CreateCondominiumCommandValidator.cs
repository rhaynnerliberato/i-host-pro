using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Structural presence checks only (Checkpoint 2 plan, item 6) — mirrors
/// <c>CreateUserCommandValidator</c>'s reasoning: format-specific rules (zip
/// code shape, field lengths) are enforced by the handler reusing
/// <c>Domain.ValueObjects.Address.Create</c>, never duplicated here.
/// <see cref="CreateCondominiumCommand.TenantId"/>/<see cref="CreateCondominiumCommand.ActorId"/>
/// are claim-sourced, never client input.
/// </summary>
public sealed class CreateCondominiumCommandValidator : AbstractValidator<CreateCondominiumCommand>
{
    public CreateCondominiumCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");

        RuleFor(x => x.Name)
            .NotEmpty()
            .WithErrorCode("name_required")
            .WithMessage("name_required");

        RuleFor(x => x.Address)
            .NotNull()
            .WithErrorCode("address_required")
            .WithMessage("address_required");

        When(x => x.Address is not null, () =>
        {
            RuleFor(x => x.Address!.ZipCode).NotEmpty().WithErrorCode("address_zip_code_required").WithMessage("address_zip_code_required");
            RuleFor(x => x.Address!.Street).NotEmpty().WithErrorCode("address_street_required").WithMessage("address_street_required");
            RuleFor(x => x.Address!.Number).NotEmpty().WithErrorCode("address_number_required").WithMessage("address_number_required");
            RuleFor(x => x.Address!.Neighborhood).NotEmpty().WithErrorCode("address_neighborhood_required").WithMessage("address_neighborhood_required");
            RuleFor(x => x.Address!.City).NotEmpty().WithErrorCode("address_city_required").WithMessage("address_city_required");
            RuleFor(x => x.Address!.State).NotEmpty().WithErrorCode("address_state_required").WithMessage("address_state_required");
        });
    }
}
