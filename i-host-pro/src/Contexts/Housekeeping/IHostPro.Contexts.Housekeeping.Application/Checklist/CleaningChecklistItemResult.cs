namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <summary>
/// <see cref="UpdatedByUserId"/>/<see cref="UpdatedAtUtc"/> are <c>null</c>
/// when this item has never been toggled for the Cleaning (no persisted row
/// — <see cref="IsChecked"/> defaults to <c>false</c> in that case, never an
/// invented value).
/// </summary>
public sealed record CleaningChecklistItemResult(
    Guid CleaningId,
    string ItemType,
    bool IsChecked,
    Guid? UpdatedByUserId,
    DateTimeOffset? UpdatedAtUtc);
