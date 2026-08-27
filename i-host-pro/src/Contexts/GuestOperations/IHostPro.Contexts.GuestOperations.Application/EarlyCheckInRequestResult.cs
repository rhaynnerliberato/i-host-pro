namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// The read-facing shape of an <c>EarlyCheckInRequest</c> returned by
/// <see cref="RequestEarlyCheckInCommand"/> (Fase 10, Checkpoint 3) — mirrors
/// <see cref="GuestStayOperationResult"/>'s own convention: <see cref="Status"/>/
/// <see cref="DenialReasonCode"/> are stable lowercase codes
/// (<see cref="EarlyCheckInRequestStatusCodeMapper"/>), never the raw Domain
/// enum. <see cref="DenialReasonCode"/> is <c>null</c> unless
/// <see cref="Status"/> is <c>"denied"</c>.
/// </summary>
public sealed record EarlyCheckInRequestResult(
    Guid Id,
    Guid ReservationId,
    DateTimeOffset RequestedCheckInAt,
    string Status,
    string? DenialReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    DateTimeOffset UpdatedAtUtc);
