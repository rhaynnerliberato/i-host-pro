namespace IHostPro.Contexts.GuestOperations.Api.Contracts;

/// <summary>
/// HTTP response shape for a <c>GuestStayOperation</c> (Fase 10, Checkpoint
/// 2 — Check-in/Checkout Core) — a thin, direct projection of
/// <c>GuestStayOperationResult</c> (Application), never the domain
/// aggregate itself.
/// </summary>
public sealed record GuestStayOperationResponse(
    Guid Id,
    Guid ReservationId,
    Guid PropertyId,
    string Status,
    DateTimeOffset? CheckedInAtUtc,
    DateTimeOffset? CheckedOutAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
