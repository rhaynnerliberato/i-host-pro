using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when a <c>LateCheckoutRequest</c> is automatically approved
/// (Fase 10, Checkpoint 3) — never published for the
/// <c>PendingPayment</c> outcome, only for a true, final approval.
/// Two independent reactions exist for this event: Workflow Orchestration's
/// reschedule orchestrator (always), sending
/// <c>Reservations.Contracts.RescheduleReservationForLateCheckout</c>
/// (ADR-018); and Housekeeping's own consumer, gated on
/// <see cref="UpdatesCleaning"/> (ADR-020 second consumer) — deliberately
/// proven as a real, wired reaction this checkpoint WITHOUT inventing a
/// schedule-offset calculation (no concrete rule for one exists in Documento
/// 10 yet). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the LateCheckoutRequest's
/// id/<c>"LateCheckoutRequest"</c>. <see cref="IntegrationEvent.ActorType"/>
/// is always <c>"System"</c>.
/// </summary>
public sealed record LateCheckoutApproved : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required DateTimeOffset ApprovedCheckOutAt { get; init; }

    /// <summary>
    /// A snapshot of the resolved <c>LateCheckoutPolicy.UpdatesCleaning</c> at
    /// decision time — the sole gate for Housekeeping's reaction. Not
    /// persisted on the LateCheckoutRequest aggregate itself (transient,
    /// decision-time-only signal), always re-derived from the same policy
    /// read the approval decision itself used.
    /// </summary>
    public required bool UpdatesCleaning { get; init; }
}
