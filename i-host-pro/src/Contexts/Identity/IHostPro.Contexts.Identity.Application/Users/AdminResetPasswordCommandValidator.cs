using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Structural presence checks only (Incremento 3, Checkpoint 9) — mirrors
/// <c>BlockUserCommandValidator</c>'s shape for the claim-sourced
/// <see cref="AdminResetPasswordCommand.TenantId"/>/<see cref="AdminResetPasswordCommand.ActorId"/>
/// and the route-supplied <see cref="AdminResetPasswordCommand.TargetUserId"/>.
/// Password POLICY is deliberately NOT checked here: enforced by the handler
/// reusing <c>IUserProvisioningService.ValidatePasswordAsync</c>, the single
/// source of truth for the policy.
/// </summary>
public sealed class AdminResetPasswordCommandValidator : AbstractValidator<AdminResetPasswordCommand>
{
    public AdminResetPasswordCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.TargetUserId).NotEmpty().WithErrorCode("target_user_id_required").WithMessage("target_user_id_required");
        RuleFor(x => x.NewPassword).NotEmpty().WithErrorCode("new_password_required").WithMessage("new_password_required");
    }
}
