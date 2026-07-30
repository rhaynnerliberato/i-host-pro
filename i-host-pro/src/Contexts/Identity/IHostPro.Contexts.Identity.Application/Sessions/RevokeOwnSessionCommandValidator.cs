using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Mirrors <c>LogoutCommandValidator</c>. <see cref="RevokeOwnSessionCommand.TenantId"/>/
/// <see cref="RevokeOwnSessionCommand.UserId"/> guard only against a
/// caller-side bug (a claim that failed to resolve to a real id) — the
/// controller never constructs this command from untrusted input for those
/// two. <see cref="RevokeOwnSessionCommand.SessionId"/> IS client-supplied
/// (the route parameter) — an empty GUID is syntactically valid for
/// <c>{sessionId:guid}</c>, so this guard has genuine value there too, ahead
/// of a needless PostgreSQL round trip the handler's ownership check would
/// otherwise perform.
/// </summary>
public sealed class RevokeOwnSessionCommandValidator : AbstractValidator<RevokeOwnSessionCommand>
{
    public RevokeOwnSessionCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode("user_id_required").WithMessage("user_id_required");
        RuleFor(x => x.SessionId).NotEmpty().WithErrorCode("session_id_required").WithMessage("session_id_required");
    }
}
