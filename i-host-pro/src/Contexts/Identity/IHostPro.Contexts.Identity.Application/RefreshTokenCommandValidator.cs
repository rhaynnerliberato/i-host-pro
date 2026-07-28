using FluentValidation;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Validates <see cref="RefreshTokenCommand"/> before it reaches any handler
/// (Incremento 2 plan, Etapa 8). Deliberately light: only non-emptiness and
/// the shared defensive length bound (<see cref="RefreshTokenLimits.MaxTotalLength"/>,
/// also enforced by the strict parser in Infrastructure, Etapa 7) are
/// checked here — the actual strict format rules (segment count, canonical
/// GUID form, base64url alphabet) belong exclusively to
/// <c>IRefreshTokenParser</c>, so they exist in exactly one place.
///
/// See <see cref="LoginCommandValidator"/> for why every rule sets both
/// <c>ErrorCode</c> and message to the same stable ASCII code.
/// </summary>
public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        // Never .Trim()/normalize the presented value — it is hashed exactly
        // as received (Incremento 2 plan, Etapa 7); silently altering it here
        // would let a different string be treated as the same token.
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithErrorCode("refresh_token_required").WithMessage("refresh_token_required")
            .MaximumLength(RefreshTokenLimits.MaxTotalLength)
                .WithErrorCode("refresh_token_too_long").WithMessage("refresh_token_too_long");

        RuleFor(x => x.RequestContext)
            .NotNull().WithErrorCode("request_context_required").WithMessage("request_context_required");
    }
}
