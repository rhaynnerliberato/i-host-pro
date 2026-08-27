namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Cross-context command (Fase 10, Checkpoint 3 — Early Check-in/Late
/// Checkout; mirrors <see cref="CloseReservation"/>'s own shape exactly): a
/// request for Reservations to move the reservation's own
/// <c>CheckInAt</c> earlier, following a real, already-approved
/// <c>EarlyCheckinApproved</c> decision. Sent exclusively by Workflow
/// Orchestration (ADR-018) — Guest Operations never calls Reservations
/// directly. <see cref="NewCheckInAt"/> is the already-decided, already
/// policy/schedule-validated new value — this command performs no
/// evaluation of its own, only the mutation via
/// <c>Reservation.Reschedule(...)</c>.
/// </summary>
public sealed record RescheduleReservationForEarlyCheckIn
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    public required DateTimeOffset NewCheckInAt { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
