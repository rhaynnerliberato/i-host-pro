using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Structural presence checks only (Incremento 3, Checkpoint 5) — mirrors
/// <c>LogoutCommandValidator</c>'s reasoning for <see cref="CreateUserCommand.TenantId"/>/
/// <see cref="CreateUserCommand.ActorId"/> (claim-sourced, never client
/// input). Email FORMAT and password POLICY are deliberately NOT checked
/// here: both are enforced by the handler reusing the real
/// <c>Domain.ValueObjects.Email.Create</c>/<c>IUserProvisioningService</c>
/// respectively — re-implementing either check here would risk diverging
/// from the single source of truth (Incremento 3, Checkpoint 5, Section 2:
/// "reutilizar integralmente").
/// </summary>
public sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.FullName).NotEmpty().WithErrorCode("full_name_required").WithMessage("full_name_required");
        RuleFor(x => x.Email).NotEmpty().WithErrorCode("email_required").WithMessage("email_required");
        RuleFor(x => x.InitialPassword).NotEmpty().WithErrorCode("initial_password_required").WithMessage("initial_password_required");
        RuleFor(x => x.RoleCode).NotEmpty().WithErrorCode("role_code_required").WithMessage("role_code_required");
    }
}
