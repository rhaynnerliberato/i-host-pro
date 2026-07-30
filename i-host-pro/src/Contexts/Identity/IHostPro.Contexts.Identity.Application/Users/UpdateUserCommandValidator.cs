using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Structural presence checks only (Incremento 3, Checkpoint 8) — mirrors
/// <c>CreateUserCommandValidator</c>'s reasoning: <see cref="UpdateUserCommand.TenantId"/>/
/// <see cref="UpdateUserCommand.ActorId"/> are claim-sourced, never client
/// input. Email FORMAT is deliberately NOT checked here — enforced by the
/// handler reusing <c>Domain.ValueObjects.Email.Create</c>, exactly like
/// CreateUser (Section 2: "reutilizar integralmente"). "At least one field
/// provided" is deliberately NOT a validator rule either: Section 3's
/// <c>Identity.NoChangesProvided</c> is a stable business-rule code, not an
/// ad hoc FluentValidation one, so the handler checks it directly (see
/// <see cref="UpdateUserCommandHandler"/>).
/// </summary>
public sealed class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.TargetUserId).NotEmpty().WithErrorCode("target_user_id_required").WithMessage("target_user_id_required");

        // A supplied-but-blank fullName ("" or whitespace only) is invalid
        // input, distinct from an omitted (null) one — Section 3: "strings
        // vazias ou compostas apenas por espaços não são valores válidos".
        RuleFor(x => x.FullName)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .When(x => x.FullName is not null)
            .WithErrorCode("full_name_invalid").WithMessage("full_name_invalid");

        // Email's equivalent blank check is deliberately NOT duplicated here:
        // Domain.ValueObjects.Email.Create already rejects an empty/whitespace
        // value with the same ArgumentException path the handler uses for an
        // invalid format, so there is nothing this rule would add.
    }
}
