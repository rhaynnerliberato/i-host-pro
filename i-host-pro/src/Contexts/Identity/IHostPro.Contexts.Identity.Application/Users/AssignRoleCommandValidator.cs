using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Mirrors <c>CreateUserCommandValidator</c>'s shape: guards only against
/// caller-side bugs (an unresolved claim, an empty route value) and an empty
/// body field — role-EXISTENCE/already-assigned/catalog checks are the
/// handler's job, reusing real components (Incremento 3, Checkpoint 6,
/// Section 3).
/// </summary>
public sealed class AssignRoleCommandValidator : AbstractValidator<AssignRoleCommand>
{
    public AssignRoleCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.TargetUserId).NotEmpty().WithErrorCode("target_user_id_required").WithMessage("target_user_id_required");
        RuleFor(x => x.RoleCode).NotEmpty().WithErrorCode("role_code_required").WithMessage("role_code_required");
    }
}
