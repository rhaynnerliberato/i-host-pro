using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Workflow.Application;

/// <summary>
/// The fourth trigger→action use case this context implements (Fase 10,
/// Checkpoint 3 — Early Check-in / Late Checkout), mirroring
/// <see cref="EarlyCheckinApprovedRescheduleOrchestrator"/>'s own shape
/// exactly. <see cref="LateCheckoutApproved.UpdatesCleaning"/> is
/// deliberately never read here — this orchestrator's sole job is the
/// reschedule; Housekeeping's own, separate consumer of the SAME event is
/// what reacts to that flag (Fase 10, Checkpoint 3 mandate — ADR-020 second
/// consumer). Never published for a <c>PendingPayment</c> outcome, so this
/// orchestrator never runs for one — Reservation's schedule stays untouched
/// until Fase 10, Checkpoint 5 resolves the payment.
/// </summary>
public sealed class LateCheckoutApprovedRescheduleOrchestrator : IIntegrationEventHandler<LateCheckoutApproved>
{
    private const string WorkflowName = "Workflow04_LateCheckoutApproved";
    private const string ActorType = "System";

    private readonly IWorkflowCommandDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LateCheckoutApprovedRescheduleOrchestrator> _logger;

    public LateCheckoutApprovedRescheduleOrchestrator(
        IWorkflowCommandDispatcher dispatcher, TimeProvider timeProvider, ILogger<LateCheckoutApprovedRescheduleOrchestrator> logger)
    {
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(LateCheckoutApproved @event, CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        var command = new RescheduleReservationForLateCheckout
        {
            TenantId = @event.TenantId,
            ReservationId = @event.ReservationId,
            NewCheckOutAt = @event.ApprovedCheckOutAt,
            CorrelationId = @event.CorrelationId,
            CausationId = @event.EventId,
        };

        try
        {
            await _dispatcher.DispatchRescheduleForLateCheckoutAsync(command, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): dispatched {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(LateCheckoutApproved), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(RescheduleReservationForLateCheckout), "CommandDispatched", durationMs);
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(ex,
                "Workflow {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
                "(source event {SourceEventId}, correlation {CorrelationId}): FAILED to dispatch {Action} — result {Result} in {DurationMs}ms",
                WorkflowName, nameof(LateCheckoutApproved), ActorType, @event.TenantId, @event.ReservationId,
                @event.EventId, @event.CorrelationId, nameof(RescheduleReservationForLateCheckout), "CommandDispatchFailed", durationMs);

            throw;
        }
    }
}
