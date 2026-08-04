using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Structural presence checks only (Checkpoint 5 plan, item 11) — mirrors
/// every other Command validator in this Bounded Context. Eligibility
/// (existence/active/role) is a business rule the handler checks against
/// Identity's contract, never here.
/// </summary>
public sealed class LinkPropertyOwnerCommandValidator : AbstractValidator<LinkPropertyOwnerCommand>
{
    public LinkPropertyOwnerCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.PropertyId).NotEmpty().WithErrorCode("property_id_required").WithMessage("property_id_required");
        RuleFor(x => x.OwnerUserId).NotEmpty().WithErrorCode("owner_user_id_required").WithMessage("owner_user_id_required");
    }
}
