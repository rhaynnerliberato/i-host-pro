using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>Mirrors <see cref="AssignRoleCommandValidator"/> exactly — see its own doc comment.</summary>
public sealed class RemoveRoleCommandValidator : AbstractValidator<RemoveRoleCommand>
{
    public RemoveRoleCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.TargetUserId).NotEmpty().WithErrorCode("target_user_id_required").WithMessage("target_user_id_required");
        RuleFor(x => x.RoleCode).NotEmpty().WithErrorCode("role_code_required").WithMessage("role_code_required");
    }
}
