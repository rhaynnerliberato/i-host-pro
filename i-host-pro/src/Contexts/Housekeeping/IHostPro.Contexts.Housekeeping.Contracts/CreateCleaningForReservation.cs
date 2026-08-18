namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Cross-context command (Fase 8, Checkpoint 1 — ADR-018): a request for
/// Housekeeping to create the Cleaning associated with a newly created
/// Reservation. Sent exclusively by Workflow Orchestration, the only
/// Bounded Context architecturally authorized to send commands (not only
/// consume events) to other contexts (<c>Architecture Principles.md</c>
/// §9/§14). Deliberately does NOT inherit <see cref="IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent"/>
/// — a command is a request for Housekeeping to do something, never a fact
/// that already happened (ADR-018's own central distinction).
///
/// Payload is intentionally minimal: no guest name/phone, no financial
/// data, nothing beyond what Housekeeping's own <c>CreateCleaningCommand</c>
/// already requires. <see cref="CorrelationId"/>/<see cref="CausationId"/>
/// mirror the triggering <c>ReservationCreated</c> event exactly, for
/// end-to-end tracing across the Workflow → Housekeeping hop.
/// </summary>
public sealed record CreateCleaningForReservation
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
