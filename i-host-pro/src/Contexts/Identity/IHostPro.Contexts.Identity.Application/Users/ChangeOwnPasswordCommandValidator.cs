using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Structural presence checks only (Incremento 3, Checkpoint 9) — mirrors
/// <c>RevokeOwnSessionCommandValidator</c>'s reasoning for
/// <see cref="ChangeOwnPasswordCommand.TenantId"/>/<see cref="ChangeOwnPasswordCommand.UserId"/>
/// (claim-sourced, guards only against a caller-side bug). Password POLICY is
/// deliberately NOT checked here: enforced by the handler reusing
/// <c>IUserProvisioningService.ValidatePasswordAsync</c>, the single source of
/// truth for the policy (Section 2: "reutilizar as abstrações existentes").
/// </summary>
public sealed class ChangeOwnPasswordCommandValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode("user_id_required").WithMessage("user_id_required");
        RuleFor(x => x.CurrentPassword).NotEmpty().WithErrorCode("current_password_required").WithMessage("current_password_required");
        RuleFor(x => x.NewPassword).NotEmpty().WithErrorCode("new_password_required").WithMessage("new_password_required");
    }
}
