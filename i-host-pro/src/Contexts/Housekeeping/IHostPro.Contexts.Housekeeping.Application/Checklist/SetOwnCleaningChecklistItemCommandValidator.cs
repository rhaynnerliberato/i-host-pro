using FluentValidation;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

public sealed class SetOwnCleaningChecklistItemCommandValidator : AbstractValidator<SetOwnCleaningChecklistItemCommand>
{
    private static readonly string[] ValidTypeCodes =
        Enum.GetValues<ChecklistItemType>().Select(ChecklistItemTypeCodeMapper.ToCode).ToArray();

    public SetOwnCleaningChecklistItemCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithErrorCode("tenant_id_required").WithMessage("tenant_id_required");
        RuleFor(x => x.ActorId).NotEmpty().WithErrorCode("actor_id_required").WithMessage("actor_id_required");
        RuleFor(x => x.CleaningId).NotEmpty().WithErrorCode("cleaning_id_required").WithMessage("cleaning_id_required");

        RuleFor(x => x.ItemType)
            .Must(type => ValidTypeCodes.Contains(type))
            .WithErrorCode("checklist_item_type_invalid")
            .WithMessage("checklist_item_type_invalid");
    }
}
