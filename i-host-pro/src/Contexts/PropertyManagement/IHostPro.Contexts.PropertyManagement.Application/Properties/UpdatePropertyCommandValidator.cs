using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Structural presence checks only (Checkpoint 3 plan, item 4/5) — mirrors
/// <c>UpdateCondominiumCommandValidator</c>'s reasoning, gated on each
/// field's <see cref="Optional{T}.IsSet"/> rather than a plain null check, so
/// "omitted" is never accidentally validated as if it were "supplied".
/// "At least one field provided" is deliberately NOT a validator rule — the
/// handler checks <see cref="Errors.PropertyManagementErrorCodes.NoChangesProvided"/>
/// directly. Format-specific rules (code shape, zip code shape) are enforced
/// by the handler reusing <c>PropertyCode.Create</c>/<c>Address.Create</c>.
/// </summary>
public sealed class UpdatePropertyCommandValidator : AbstractValidator<UpdatePropertyCommand>
{
    public UpdatePropertyCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.PropertyId).NotEmpty().WithErrorCode("property_id_required").WithMessage("property_id_required");

        // A supplied-but-blank code/name is invalid input, distinct from an
        // omitted one — and an explicit null for either (a field with no
        // meaningful "unset" value of its own) is equally invalid.
        RuleFor(x => x.Code.Value)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .When(x => x.Code.IsSet)
            .WithErrorCode("code_invalid").WithMessage("code_invalid");

        RuleFor(x => x.Name.Value)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .When(x => x.Name.IsSet)
            .WithErrorCode("name_invalid").WithMessage("name_invalid");

        RuleFor(x => x.Capacity.Value)
            .Must(value => value is > 0)
            .When(x => x.Capacity.IsSet)
            .WithErrorCode("capacity_must_be_positive").WithMessage("capacity_must_be_positive");

        When(x => x.Address.IsSet && x.Address.Value is not null, () =>
        {
            RuleFor(x => x.Address.Value!.ZipCode).NotEmpty().WithErrorCode("address_zip_code_required").WithMessage("address_zip_code_required");
            RuleFor(x => x.Address.Value!.Street).NotEmpty().WithErrorCode("address_street_required").WithMessage("address_street_required");
            RuleFor(x => x.Address.Value!.Number).NotEmpty().WithErrorCode("address_number_required").WithMessage("address_number_required");
            RuleFor(x => x.Address.Value!.Neighborhood).NotEmpty().WithErrorCode("address_neighborhood_required").WithMessage("address_neighborhood_required");
            RuleFor(x => x.Address.Value!.City).NotEmpty().WithErrorCode("address_city_required").WithMessage("address_city_required");
            RuleFor(x => x.Address.Value!.State).NotEmpty().WithErrorCode("address_state_required").WithMessage("address_state_required");
        });
    }
}
