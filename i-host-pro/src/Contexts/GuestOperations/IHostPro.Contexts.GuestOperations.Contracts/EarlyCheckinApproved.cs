using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when an <c>EarlyCheckInRequest</c> is automatically approved
/// (Fase 10, Checkpoint 3) — the sole trigger Workflow Orchestration's
/// reschedule orchestrator reacts to, sending
/// <c>Reservations.Contracts.RescheduleReservationForEarlyCheckIn</c> in
/// response (ADR-018: Guest Operations never calls Reservations directly).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the EarlyCheckInRequest's id/<c>"EarlyCheckInRequest"</c>.
/// <see cref="IntegrationEvent.ActorType"/> is always <c>"System"</c> — the
/// decision is automatic, synchronous, made in the same request as its own
/// creation, never a distinct human-actor approval step. <see cref="PropertyId"/>
/// was added in Fase 10, Checkpoint 4 (Portaria Notification Foundation) —
/// Communication's new Front Desk processor needs it to resolve the current
/// front desk contact via the new synchronous exception #9
/// (<c>IFrontDeskContactReader</c>, ADR-026) without a second lookup; it is
/// provider-neutral and non-PII — no guest name/phone/any other
/// business-sensitive content belongs here.
/// </summary>
public sealed record EarlyCheckinApproved : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required DateTimeOffset ApprovedCheckInAt { get; init; }
}
