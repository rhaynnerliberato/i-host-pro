namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// The read-facing shape of a <c>GuestStayOperation</c> returned by
/// check-in/checkout commands (Fase 10, Checkpoint 2 — Check-in/Checkout
/// Core) — mirrors <c>Reservations.Application.Reservations.ReservationResult</c>'s
/// own convention: <see cref="Status"/> is the stable lowercase code
/// (<see cref="GuestStayOperationStatusCodeMapper"/>), never the raw Domain
/// enum.
/// </summary>
public sealed record GuestStayOperationResult(
    Guid Id,
    Guid ReservationId,
    Guid PropertyId,
    string Status,
    DateTimeOffset? CheckedInAtUtc,
    DateTimeOffset? CheckedOutAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
