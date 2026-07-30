using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>Mirrors <c>AssignRoleCommandValidator</c>'s shape: guards only against caller-side bugs (an unresolved claim, an empty route value).</summary>
public sealed class BlockUserCommandValidator : AbstractValidator<BlockUserCommand>
{
    public BlockUserCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.TargetUserId).NotEmpty().WithErrorCode("target_user_id_required").WithMessage("target_user_id_required");
    }
}
