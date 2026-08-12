namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

public sealed record CleaningOccurrenceResponse(
    Guid Id,
    Guid CleaningId,
    string Type,
    string? Description,
    Guid RegisteredByUserId,
    DateTimeOffset RegisteredAtUtc);
