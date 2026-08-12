using FluentValidation;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

public sealed class RegisterCleaningOccurrenceCommandValidator : AbstractValidator<RegisterCleaningOccurrenceCommand>
{
    public const int MaxDescriptionLength = 500;

    private static readonly string[] ValidTypeCodes =
        Enum.GetValues<OccurrenceType>().Select(OccurrenceTypeCodeMapper.ToCode).ToArray();

    public RegisterCleaningOccurrenceCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.CleaningId).NotEmpty().WithErrorCode("cleaning_id_required").WithMessage("cleaning_id_required");

        RuleFor(x => x.Type)
            .Must(type => ValidTypeCodes.Contains(type))
            .WithErrorCode("occurrence_type_invalid")
            .WithMessage("occurrence_type_invalid");

        RuleFor(x => x.Description)
            .MaximumLength(MaxDescriptionLength)
            .When(x => x.Description is not null)
            .WithErrorCode("description_too_long")
            .WithMessage("description_too_long");
    }
}
