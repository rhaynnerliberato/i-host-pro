using FluentValidation;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Validates <see cref="LoginCommand"/> before it reaches any handler
/// (Incremento 2 plan, Etapa 8), via the existing generic
/// <c>ValidationBehavior</c> (<c>BuildingBlocks.Application</c>).
///
/// Every rule uses a stable, ASCII, snake_case error code as BOTH
/// <c>ErrorCode</c> and the rule's message: <c>ValidationBehavior</c> (not
/// modified in this step) currently forwards only <c>ErrorMessage</c> into
/// the final <c>Result</c>, discarding FluentValidation's own
/// <c>ErrorCode</c> — setting both keeps the codes meaningful today, through
/// the existing unmodified pipeline, while remaining correct if that
/// pipeline is later enhanced to surface <c>ErrorCode</c> directly. No rule
/// message ever includes the property's actual value — see
/// <see cref="LoginCommandValidatorTests"/> for the explicit guarantee this
/// is asserted, not merely intended.
///
/// Deliberately does NOT enforce <c>PasswordPolicyOptions.MinimumLength</c>/
/// complexity here: those rules govern setting a NEW password, not
/// authenticating with an existing one — a correct password for an account
/// created under an older, looser policy must still be accepted at login.
/// Only a defensive maximum is applied, to bound Argon2id's cost against an
/// abusively long submitted value — independent of business policy, the
/// same reasoning already applied to <see cref="RefreshTokenLimits.MaxTotalLength"/>
/// in Etapa 7.
/// </summary>
public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    /// <summary>
    /// Defensive bound against abusive Argon2id cost — not a business
    /// policy value. Comfortably above NIST 800-63B's recommendation that
    /// systems support passphrases of at least 64 characters.
    /// </summary>
    public const int MaxPasswordLength = 256;

    private const int MaxTenantSlugLength = 63; // matches TenantSlug's own upper bound (Identity.Domain)
    private const int MaxEmailLength = 320; // RFC 5321

    public LoginCommandValidator()
    {
        // Each `.When(..., ApplyConditionTo.CurrentValidator)` is deliberate:
        // FluentValidation's default `.When()` (without that second argument)
        // applies the condition to every rule already registered in the same
        // chain — including NotEmpty()/MaximumLength() above it — which would
        // silently skip the required-field check whenever the guard is false
        // (found and fixed while writing the tests below). CurrentValidator
        // restricts the guard to only the Must() format check, so an empty
        // or oversized slug/email still correctly fails NotEmpty/MaximumLength.
        RuleFor(x => x.TenantSlug)
            .NotEmpty().WithErrorCode("tenant_slug_required").WithMessage("tenant_slug_required")
            .MaximumLength(MaxTenantSlugLength).WithErrorCode("tenant_slug_too_long").WithMessage("tenant_slug_too_long")
            .Must(BeAWellFormedTenantSlug).WithErrorCode("tenant_slug_invalid_format").WithMessage("tenant_slug_invalid_format")
            .When(x => !string.IsNullOrEmpty(x.TenantSlug) && x.TenantSlug.Length <= MaxTenantSlugLength,
                ApplyConditionTo.CurrentValidator);

        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("email_required").WithMessage("email_required")
            .MaximumLength(MaxEmailLength).WithErrorCode("email_too_long").WithMessage("email_too_long")
            .Must(BeAWellFormedEmail).WithErrorCode("email_invalid_format").WithMessage("email_invalid_format")
            .When(x => !string.IsNullOrEmpty(x.Email) && x.Email.Length <= MaxEmailLength,
                ApplyConditionTo.CurrentValidator);

        // Never .Trim()/normalize Password anywhere in this rule chain — an
        // incorrect password (even one that only differs by whitespace) must
        // fail, not be silently corrected (Incremento 2 plan, Etapa 8).
        RuleFor(x => x.Password)
            .NotEmpty().WithErrorCode("password_required").WithMessage("password_required")
            .MaximumLength(MaxPasswordLength).WithErrorCode("password_too_long").WithMessage("password_too_long");

        RuleFor(x => x.RequestContext)
            .NotNull().WithErrorCode("request_context_required").WithMessage("request_context_required");
    }

    private static bool BeAWellFormedTenantSlug(string slug)
    {
        try
        {
            TenantSlug.Create(slug);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool BeAWellFormedEmail(string email)
    {
        try
        {
            Email.Create(email);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
