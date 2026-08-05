using FluentValidation;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Structural presence/format checks only (Fase 3, Incremento 1 plan, item
/// 6) — business rules that need a lookup (property must exist/be
/// <c>Active</c>, guest count vs. capacity, date conflict) are enforced by
/// the handler, never duplicated here (mirrors
/// <c>CreatePropertyCommandValidator</c>'s own reasoning).
/// </summary>
public sealed class CreateReservationCommandValidator : AbstractValidator<CreateReservationCommand>
{
    public const int MaxGuestNameLength = 150;
    public const int MaxGuestPhoneLength = 30;

    public CreateReservationCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.PropertyId).NotEmpty().WithErrorCode("property_id_required").WithMessage("property_id_required");

        RuleFor(x => x.GuestName)
            .NotEmpty().WithErrorCode("guest_name_required").WithMessage("guest_name_required")
            .MaximumLength(MaxGuestNameLength).WithErrorCode("guest_name_too_long").WithMessage("guest_name_too_long");

        RuleFor(x => x.GuestPhone)
            .MaximumLength(MaxGuestPhoneLength)
            .When(x => x.GuestPhone is not null)
            .WithErrorCode("guest_phone_too_long").WithMessage("guest_phone_too_long");

        RuleFor(x => x.GuestCount)
            .GreaterThan(0)
            .WithErrorCode("guest_count_must_be_positive")
            .WithMessage("guest_count_must_be_positive");

        RuleFor(x => x.CheckOutAt)
            .GreaterThan(x => x.CheckInAt)
            .WithErrorCode("check_out_must_be_after_check_in")
            .WithMessage("check_out_must_be_after_check_in");
    }
}
