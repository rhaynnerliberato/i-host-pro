using FluentValidation;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Structural presence/format checks only (Fase 3, Incremento 1 plan, item
/// 8), gated on each field's <see cref="Optional{T}.IsSet"/> rather than a
/// plain default-value check, so "omitted" is never accidentally validated
/// as if it were "supplied" — mirrors <c>UpdatePropertyCommandValidator</c>'s
/// reasoning. "At least one field provided" is deliberately NOT a validator
/// rule — the handler checks <c>NoChangesProvided</c> directly. The
/// combined-final-state check (check-out after check-in once both omitted/
/// supplied values are resolved) is a business rule the handler performs,
/// never this validator.
/// </summary>
public sealed class UpdateReservationCommandValidator : AbstractValidator<UpdateReservationCommand>
{
    public UpdateReservationCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.ReservationId).NotEmpty().WithErrorCode("reservation_id_required").WithMessage("reservation_id_required");

        RuleFor(x => x.PropertyId.Value)
            .NotEqual(Guid.Empty)
            .When(x => x.PropertyId.IsSet)
            .WithErrorCode("property_id_invalid").WithMessage("property_id_invalid");

        RuleFor(x => x.GuestName.Value)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .When(x => x.GuestName.IsSet)
            .WithErrorCode("guest_name_invalid").WithMessage("guest_name_invalid");

        RuleFor(x => x.GuestName.Value)
            .MaximumLength(CreateReservationCommandValidator.MaxGuestNameLength)
            .When(x => x.GuestName.IsSet && x.GuestName.Value is not null)
            .WithErrorCode("guest_name_too_long").WithMessage("guest_name_too_long");

        RuleFor(x => x.GuestPhone.Value)
            .MaximumLength(CreateReservationCommandValidator.MaxGuestPhoneLength)
            .When(x => x.GuestPhone.IsSet && x.GuestPhone.Value is not null)
            .WithErrorCode("guest_phone_too_long").WithMessage("guest_phone_too_long");

        RuleFor(x => x.CheckInAt.Value)
            .NotEqual(default(DateTimeOffset))
            .When(x => x.CheckInAt.IsSet)
            .WithErrorCode("check_in_at_invalid").WithMessage("check_in_at_invalid");

        RuleFor(x => x.CheckOutAt.Value)
            .NotEqual(default(DateTimeOffset))
            .When(x => x.CheckOutAt.IsSet)
            .WithErrorCode("check_out_at_invalid").WithMessage("check_out_at_invalid");

        RuleFor(x => x.GuestCount.Value)
            .GreaterThan(0)
            .When(x => x.GuestCount.IsSet)
            .WithErrorCode("guest_count_must_be_positive").WithMessage("guest_count_must_be_positive");
    }
}
