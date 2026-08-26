using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when a <c>GuestStayOperation</c> transitions to CheckedOut
/// (Fase 10, Checkpoint 1 — Guest Operations Foundation) — the sole trigger
/// Workflow Orchestration's new orchestrator reacts to, sending
/// <c>Reservations.Contracts.CloseReservation</c> in response.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the GuestStayOperation's id/<c>"GuestStayOperation"</c>.
/// <see cref="IntegrationEvent.ActorType"/> is always <c>"System"</c> this
/// checkpoint — no front-desk/HTTP actor exists yet (CP1 has zero public API
/// endpoints). Payload carries only <see cref="ReservationId"/> — no
/// PropertyId (no CP1 consumer needs it; <c>CloseReservation</c>'s own
/// payload does not require it either, Reservations already owns it) and no
/// guest name/phone/any other business-sensitive content.
/// </summary>
public sealed record GuestCheckedOut : IntegrationEvent
{
    public required Guid ReservationId { get; init; }
}
