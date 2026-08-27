using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Workflow.Application;

/// <summary>
/// The third trigger→action use case this context implements (Fase 10,
/// Checkpoint 3 — Early Check-in / Late Checkout), mirroring
/// <see cref="GuestCheckedOutCloseReservationOrchestrator"/>'s own shape
/// exactly: a pure, stateless transformation — reads only fields already
/// present on <see cref="EarlyCheckinApproved"/>, touches no persistence, and
/// dispatches exactly one command via <see cref="IWorkflowCommandDispatcher"/>.
/// Guest Operations never calls Reservations directly (ADR-018) — this
/// orchestrator is the only path from an approved Early Check-in request to
/// the Reservation's own schedule actually changing.
/// </summary>
public sealed class EarlyCheckinApprovedRescheduleOrchestrator : IIntegrationEventHandler<EarlyCheckinApproved>
{
    private const string WorkflowName = "Workflow03_EarlyCheckinApproved";
    private const string ActorType = "System";

    private readonly IWorkflowCommandDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EarlyCheckinApprovedRescheduleOrchestrator> _logger;

    public EarlyCheckinApprovedRescheduleOrchestrator(
        IWorkflowCommandDispatcher dispatcher, TimeProvider timeProvider, ILogger<EarlyCheckinApprovedRescheduleOrchestrator> logger)
    {
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(EarlyCheckinApproved @event, CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        var command = new RescheduleReservationForEarlyCheckIn
        {
            TenantId = @event.TenantId,
            ReservationId = @event.ReservationId,
            NewCheckInAt = @event.ApprovedCheckInAt,
            CorrelationId = @event.CorrelationId,
            CausationId = @event.EventId,
        };

        try
        {
            await _dispatcher.DispatchRescheduleForEarlyCheckInAsync(command, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): dispatched {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(EarlyCheckinApproved), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(RescheduleReservationForEarlyCheckIn), "CommandDispatched", durationMs);
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(ex,
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): FAILED to dispatch {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(EarlyCheckinApproved), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(RescheduleReservationForEarlyCheckIn), "CommandDispatchFailed", durationMs);

            throw;
        }
    }
}
