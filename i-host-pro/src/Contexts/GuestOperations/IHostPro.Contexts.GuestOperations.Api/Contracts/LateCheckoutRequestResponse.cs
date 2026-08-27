namespace IHostPro.Contexts.GuestOperations.Api.Contracts;

/// <summary>
/// HTTP response shape for a <c>LateCheckoutRequest</c> (Fase 10, Checkpoint
/// 3) — a thin, direct projection of <c>LateCheckoutRequestResult</c>
/// (Application), never the domain aggregate itself.
/// </summary>
public sealed record LateCheckoutRequestResponse(
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
