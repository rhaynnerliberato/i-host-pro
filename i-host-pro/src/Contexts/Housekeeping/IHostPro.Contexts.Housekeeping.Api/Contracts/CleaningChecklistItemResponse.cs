namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

public sealed record CleaningChecklistItemResponse(
    Guid CleaningId,
    string ItemType,
    bool IsChecked,
    Guid? UpdatedByUserId,
    DateTimeOffset? UpdatedAtUtc);
