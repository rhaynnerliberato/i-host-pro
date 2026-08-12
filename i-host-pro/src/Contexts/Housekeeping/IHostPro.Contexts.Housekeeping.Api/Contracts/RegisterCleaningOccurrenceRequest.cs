namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

/// <summary>Every field is nullable/default at the wire level — presence/validity is validated by the Application layer.</summary>
public sealed record RegisterCleaningOccurrenceRequest(string? Type, string? Description);
