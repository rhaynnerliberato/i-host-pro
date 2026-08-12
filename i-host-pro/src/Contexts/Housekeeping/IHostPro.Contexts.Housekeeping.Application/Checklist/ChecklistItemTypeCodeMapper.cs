using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <summary>Mirrors <c>Cleanings.CleaningStatusCodeMapper</c> exactly — an explicit switch, never <c>ToString()</c>.</summary>
public static class ChecklistItemTypeCodeMapper
{
    public static string ToCode(ChecklistItemType type) => type switch
    {
        ChecklistItemType.Stove => "Stove",
        ChecklistItemType.Refrigerator => "Refrigerator",
        ChecklistItemType.Tv => "Tv",
        ChecklistItemType.AirConditioning => "AirConditioning",
        ChecklistItemType.Bathroom => "Bathroom",
        ChecklistItemType.Linens => "Linens",
        ChecklistItemType.Trash => "Trash",
        ChecklistItemType.Window => "Window",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped ChecklistItemType."),
    };

    public static ChecklistItemType FromCode(string code) => code switch
    {
        "Stove" => ChecklistItemType.Stove,
        "Refrigerator" => ChecklistItemType.Refrigerator,
        "Tv" => ChecklistItemType.Tv,
        "AirConditioning" => ChecklistItemType.AirConditioning,
        "Bathroom" => ChecklistItemType.Bathroom,
        "Linens" => ChecklistItemType.Linens,
        "Trash" => ChecklistItemType.Trash,
        "Window" => ChecklistItemType.Window,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped checklist item type code."),
    };
}
