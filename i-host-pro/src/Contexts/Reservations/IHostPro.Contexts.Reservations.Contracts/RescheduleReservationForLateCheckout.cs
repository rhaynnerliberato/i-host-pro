namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Cross-context command (Fase 10, Checkpoint 3 — Early Check-in/Late
/// Checkout; mirrors <see cref="CloseReservation"/>'s own shape exactly): a
/// request for Reservations to move the reservation's own
/// <c>CheckOutAt</c> later, following a real, already-approved
/// <c>LateCheckoutApproved</c> decision (never sent for a
/// <c>PendingPayment</c> outcome — see ADR-024 amendment). Sent exclusively
/// by Workflow Orchestration (ADR-018). <see cref="NewCheckOutAt"/> is the
/// already-decided, already policy/schedule-validated new value — this
/// command performs no evaluation of its own, only the mutation via
/// <c>Reservation.Reschedule(...)</c>.
/// </summary>
public sealed record RescheduleReservationForLateCheckout
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    public required DateTimeOffset NewCheckOutAt { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
