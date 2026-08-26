namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// Cross-context command (Fase 10, Checkpoint 1 — Guest Operations
/// Foundation; mirrors ADR-018's own <c>CreateCleaningForReservation</c>
/// shape exactly): a request for Reservations to close the reservation
/// associated with a guest's real checkout. Sent exclusively by Workflow
/// Orchestration, the only Bounded Context architecturally authorized to
/// send commands (not only consume events) to other contexts
/// (<c>Architecture Principles.md</c> §9/§14). Deliberately does NOT inherit
/// <see cref="IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent"/>
/// — a command is a request for Reservations to do something, never a fact
/// that already happened.
///
/// Payload is intentionally minimal: no guest name/phone, no financial data,
/// nothing beyond the reservation identity itself — Reservations already
/// owns everything else (<c>PropertyId</c> included) needed to close it.
/// <see cref="CorrelationId"/>/<see cref="CausationId"/> mirror the
/// triggering <c>GuestCheckedOut</c> event exactly, for end-to-end tracing
/// across the Guest Operations → Workflow → Reservations hop.
/// </summary>
public sealed record CloseReservation
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
