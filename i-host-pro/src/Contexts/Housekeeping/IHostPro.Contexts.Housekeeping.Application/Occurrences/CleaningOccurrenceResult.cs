namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

public sealed record CleaningOccurrenceResult(
    Guid Id,
    Guid CleaningId,
    string Type,
    string? Description,
    Guid RegisteredByUserId,
    DateTimeOffset RegisteredAtUtc);
