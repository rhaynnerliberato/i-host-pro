using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Structural presence checks only (Checkpoint 3 plan, item 3) — mirrors
/// <c>CreateCondominiumCommandValidator</c>'s reasoning: format-specific
/// rules (code shape, zip code shape, field lengths) are enforced by the
/// handler reusing <c>PropertyCode.Create</c>/<c>Address.Create</c>, never
/// duplicated here. Whether <see cref="CreatePropertyCommand.Address"/> is
/// REQUIRED (i.e., no <see cref="CreatePropertyCommand.CondominiumId"/>
/// supplied) is a business rule checked by the handler, not this validator —
/// mirrors <c>UpdateCondominiumCommandValidator</c>'s "at least one field"
/// precedent.
/// </summary>
public sealed class CreatePropertyCommandValidator : AbstractValidator<CreatePropertyCommand>
{
    public CreatePropertyCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");

        RuleFor(x => x.Code)
            .NotEmpty()
            .WithErrorCode("code_required")
            .WithMessage("code_required");

        RuleFor(x => x.Name)
            .NotEmpty()
            .WithErrorCode("name_required")
            .WithMessage("name_required");

        RuleFor(x => x.Capacity)
            .GreaterThan(0)
            .WithErrorCode("capacity_must_be_positive")
            .WithMessage("capacity_must_be_positive");

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
