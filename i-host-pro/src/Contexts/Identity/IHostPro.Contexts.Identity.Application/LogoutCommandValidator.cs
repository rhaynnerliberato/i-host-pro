using FluentValidation;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Validates <see cref="LogoutCommand"/> before it reaches any handler
/// (Incremento 2 plan, Etapa 8). Guards only against an empty identifier —
/// <see cref="LogoutCommand"/>'s constructor already makes it structurally
/// impossible to supply these from an untrusted public body (there is no
/// such parameter), so this validator's role is limited to catching a
/// caller-side bug (e.g. a claim that failed to resolve to a real id)
/// rather than an untrusted-input concern.
/// </summary>
public sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode("user_id_required").WithMessage("user_id_required");
        RuleFor(x => x.SessionId).NotEmpty().WithErrorCode("session_id_required").WithMessage("session_id_required");
    }
}
