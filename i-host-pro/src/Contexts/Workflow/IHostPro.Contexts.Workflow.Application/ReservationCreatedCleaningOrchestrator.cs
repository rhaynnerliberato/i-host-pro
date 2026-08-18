using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

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
///
/// Fase 8, Checkpoint 2.1 (corrective audit gate — Documento 17 §28,
/// proportional to a stateless single-action workflow, no new persistence):
/// emits ONE structured, PII-safe log entry per act of orchestration —
/// success or failure, never both, never silent. Fields: <c>WorkflowName</c>
/// (a fixed identifier for this workflow, not the .NET type name — stays
/// stable if the class is ever renamed), <c>Trigger</c> (the Integration
/// Event type name), <c>ActorType</c> (always <c>"System"</c> — this flow
/// has no human/AI actor), <c>TenantId</c>, <c>ReservationId</c>,
/// <c>SourceEventId</c> (<see cref="IntegrationEvent.EventId"/> of the
/// triggering <see cref="ReservationCreated"/> — the domain-level identifier
/// already carried forward as <see cref="CreateCleaningForReservation.CausationId"/>;
/// deliberately not Wolverine's own transport-internal envelope id, which
/// would require leaking Wolverine metadata into this Wolverine-free
/// project), <c>CorrelationId</c>, <c>Action</c> (the command type name),
/// <c>Result</c> (<c>"CommandDispatched"</c>/<c>"CommandDispatchFailed"</c>
/// — never <c>"CleaningCreated"</c>, which is Housekeeping's own later,
/// asynchronous outcome, not this act's result), and <c>DurationMs</c> (this
/// orchestrator's own dispatch call only — never includes Housekeeping's own
/// processing time, since the command is sent asynchronously and this
/// method never waits for it). A command identifier is deliberately absent:
/// <c>IMessageBus.SendAsync</c> returns no accessible envelope/message id in
/// Wolverine 6.22.0 (confirmed by inspection, not assumed) — the
/// <c>CorrelationId</c> already carried on the command is the available
/// substitute. No guest name/phone/address/financial data is ever in scope
/// to log — none of the fields above touch it.
/// </summary>
public sealed class ReservationCreatedCleaningOrchestrator : IIntegrationEventHandler<ReservationCreated>
{
    private const string WorkflowName = "Workflow01_NewReservation";
    private const string ActorType = "System";

    private readonly IWorkflowCommandDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReservationCreatedCleaningOrchestrator> _logger;

    public ReservationCreatedCleaningOrchestrator(
        IWorkflowCommandDispatcher dispatcher, TimeProvider timeProvider, ILogger<ReservationCreatedCleaningOrchestrator> logger)
    {
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        var command = new CreateCleaningForReservation
        {
            TenantId = @event.TenantId,
            ReservationId = @event.ReservationId,
            PropertyId = @event.PropertyId,
            CorrelationId = @event.CorrelationId,
            CausationId = @event.EventId,
        };

        try
        {
            await _dispatcher.DispatchCreateCleaningForReservationAsync(command, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): dispatched {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(ReservationCreated), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(CreateCleaningForReservation), "CommandDispatched", durationMs);
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(ex,
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): FAILED to dispatch {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(ReservationCreated), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(CreateCleaningForReservation), "CommandDispatchFailed", durationMs);

            throw;
        }
    }
}
