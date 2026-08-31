namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Minimal cleaning-status projection for a single Reservation (Fase 11,
/// Checkpoint 3 — AI Agent's own <c>GetCleaningStatus</c> Read Tool). Only
/// real persisted facts — never an invented ETA/estimate.
/// </summary>
public sealed record CleaningStatusResult(string Status, DateTimeOffset? ScheduledAtUtc, DateTimeOffset? CompletedAtUtc);
