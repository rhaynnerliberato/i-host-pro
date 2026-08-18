using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Workflow.Application;

/// <summary>
/// The sole trigger→action use case this checkpoint implements (Fase 8,
/// Checkpoint 1 — ADR-018; moved from <c>Workflow.Infrastructure</c> to this
/// Application layer at Checkpoint 1.1's corrective review, since deciding
/// that a new Reservation always requests Housekeeping create the
/// corresponding Cleaning IS the orchestration — application logic, never
/// transport plumbing): a pure, stateless transformation — reads only
/// fields already present on <see cref="ReservationCreated"/>, touches no
/// persistence, and dispatches exactly one command via
/// <see cref="IWorkflowCommandDispatcher"/> (never a Wolverine dependency
/// here — that belongs to the Infrastructure implementation of the
/// dispatcher).
/// <see cref="CreateCleaningForReservation.CorrelationId"/>/<see cref="CreateCleaningForReservation.CausationId"/>
/// carry the triggering event's own correlation/id forward, for end-to-end
/// tracing across the Workflow → Housekeeping hop.
///
/// Implements <see cref="IIntegrationEventHandler{TEvent}"/> directly so
/// Workflow.Infrastructure's keyed-DI registration and its thin Wolverine
/// adapter (<c>ReservationCreatedHandler</c>) are entirely unaffected by
/// this move — they still resolve and call
/// <see cref="IIntegrationEventHandler{TEvent}.HandleAsync"/> exactly as
/// before.
/// </summary>
public sealed class ReservationCreatedCleaningOrchestrator : IIntegrationEventHandler<ReservationCreated>
{
    private readonly IWorkflowCommandDispatcher _dispatcher;

    public ReservationCreatedCleaningOrchestrator(IWorkflowCommandDispatcher dispatcher) => _dispatcher = dispatcher;

    public Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken)
    {
        var command = new CreateCleaningForReservation
        {
            TenantId = @event.TenantId,
            ReservationId = @event.ReservationId,
            PropertyId = @event.PropertyId,
            CorrelationId = @event.CorrelationId,
            CausationId = @event.EventId,
        };

        return _dispatcher.DispatchCreateCleaningForReservationAsync(command, cancellationToken);
    }
}
