using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

/// <summary>
/// Structural presence checks only (Fase 10, Checkpoint 4) — mirrors
/// <c>CreateCondominiumCommandValidator</c>'s reasoning. No phone-number
/// format validation: this codebase has no shared phone-normalization
/// utility (confirmed by audit) — <see cref="SetFrontDeskContactCommand.PhoneNumber"/>
/// is stored trim-or-reject-if-empty only, the same convention already used
/// for <c>Reservation.GuestPhone</c>.
/// </summary>
public sealed class SetFrontDeskContactCommandValidator : AbstractValidator<SetFrontDeskContactCommand>
{
    public SetFrontDeskContactCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.CondominiumId).NotEmpty().WithErrorCode("condominium_id_required").WithMessage("condominium_id_required");

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .WithErrorCode("display_name_required")
            .WithMessage("display_name_required");

        RuleFor(x => x.PhoneNumber)
            .NotEmpty()
            .WithErrorCode("phone_number_required")
            .WithMessage("phone_number_required");
    }
}
