using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Structural presence checks only (Checkpoint 2 plan, item 6) — mirrors
/// <c>UpdateCondominiumCommandValidator</c>'s Create counterpart and
/// <c>UpdateUserCommandValidator</c>'s reasoning. "At least one field
/// provided" is deliberately NOT a validator rule — the handler checks
/// <see cref="Errors.PropertyManagementErrorCodes.NoChangesProvided"/>
/// directly, since it is a stable business-rule code, not an ad hoc
/// FluentValidation one.
/// </summary>
public sealed class UpdateCondominiumCommandValidator : AbstractValidator<UpdateCondominiumCommand>
{
    public UpdateCondominiumCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.CondominiumId).NotEmpty().WithErrorCode("condominium_id_required").WithMessage("condominium_id_required");

        // A supplied-but-blank name ("" or whitespace only) is invalid input,
        // distinct from an omitted (null) one.
        RuleFor(x => x.Name)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .When(x => x.Name is not null)
            .WithErrorCode("name_invalid").WithMessage("name_invalid");

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
