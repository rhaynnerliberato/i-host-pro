namespace IHostPro.Contexts.GuestOperations.Api.Contracts;

/// <summary>
/// HTTP response shape for an <c>EarlyCheckInRequest</c> (Fase 10, Checkpoint
/// 3) — a thin, direct projection of <c>EarlyCheckInRequestResult</c>
/// (Application), never the domain aggregate itself.
/// </summary>
public sealed record EarlyCheckInRequestResponse(
    Guid Id,
    Guid ReservationId,
    DateTimeOffset RequestedCheckInAt,
    string Status,
    string? DenialReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    DateTimeOffset UpdatedAtUtc);
