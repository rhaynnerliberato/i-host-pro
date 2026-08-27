namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// The read-facing shape of a <c>LateCheckoutRequest</c> returned by
/// <see cref="RequestLateCheckoutCommand"/> (Fase 10, Checkpoint 3) — mirrors
/// <see cref="GuestStayOperationResult"/>'s own convention: <see cref="Status"/>/
/// <see cref="DenialReasonCode"/>/<see cref="ChargeType"/> are stable lowercase
/// codes (<see cref="LateCheckoutRequestStatusCodeMapper"/>), never the raw
/// Domain enum. <see cref="DenialReasonCode"/> is <c>null</c> unless
/// <see cref="Status"/> is <c>"denied"</c>.
/// </summary>
public sealed record LateCheckoutRequestResult(
    Guid Id,
    Guid ReservationId,
    DateTimeOffset RequestedCheckOutAt,
    string ChargeType,
    decimal? ChargeValue,
    bool RequiresPix,
    string Status,
    string? DenialReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    DateTimeOffset UpdatedAtUtc);
