using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Workflow.Application;

/// <summary>
/// The second trigger→action use case this context implements (Fase 10,
/// Checkpoint 1 — Guest Operations Foundation), mirroring
/// <see cref="ReservationCreatedCleaningOrchestrator"/>'s own shape exactly:
/// a pure, stateless transformation — reads only fields already present on
/// <see cref="GuestCheckedOut"/>, touches no persistence, and dispatches
/// exactly one command via <see cref="IWorkflowCommandDispatcher"/>.
/// <see cref="CloseReservation.CorrelationId"/>/<see cref="CloseReservation.CausationId"/>
/// carry the triggering event's own correlation/id forward, for end-to-end
/// tracing across the Guest Operations → Workflow → Reservations hop.
///
/// Emits the same structured, PII-safe log entry shape
/// <see cref="ReservationCreatedCleaningOrchestrator"/> established
/// (Documento 17 §28) — success or failure, never both, never silent.
/// </summary>
public sealed class GuestCheckedOutCloseReservationOrchestrator : IIntegrationEventHandler<GuestCheckedOut>
{
    private const string WorkflowName = "Workflow02_GuestCheckedOut";
    private const string ActorType = "System";

    private readonly IWorkflowCommandDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GuestCheckedOutCloseReservationOrchestrator> _logger;

    public GuestCheckedOutCloseReservationOrchestrator(
        IWorkflowCommandDispatcher dispatcher, TimeProvider timeProvider, ILogger<GuestCheckedOutCloseReservationOrchestrator> logger)
    {
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(GuestCheckedOut @event, CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        var command = new CloseReservation
        {
            TenantId = @event.TenantId,
            ReservationId = @event.ReservationId,
            CorrelationId = @event.CorrelationId,
            CausationId = @event.EventId,
        };

        try
        {
            await _dispatcher.DispatchCloseReservationAsync(command, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): dispatched {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(GuestCheckedOut), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(CloseReservation), "CommandDispatched", durationMs);
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(ex,
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): FAILED to dispatch {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(GuestCheckedOut), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(CloseReservation), "CommandDispatchFailed", durationMs);

            throw;
        }
    }
}
