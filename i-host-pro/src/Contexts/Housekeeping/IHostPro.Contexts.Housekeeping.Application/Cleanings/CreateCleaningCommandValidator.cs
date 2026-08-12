using FluentValidation;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Structural presence checks only (Fase 6, Incremento 1) — the property
/// must be a known, active property and the reservation reference (when
/// supplied) must exist are both lookups, enforced by the handler, never
/// duplicated here (mirrors <c>CreateReservationCommandValidator</c>'s own
/// reasoning).
/// </summary>
public sealed class CreateCleaningCommandValidator : AbstractValidator<CreateCleaningCommand>
{
    public CreateCleaningCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.PropertyId).NotEmpty().WithErrorCode("property_id_required").WithMessage("property_id_required");
    }
}
