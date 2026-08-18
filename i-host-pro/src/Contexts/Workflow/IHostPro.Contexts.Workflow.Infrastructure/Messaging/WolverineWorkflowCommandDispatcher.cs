using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Workflow.Application;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace IHostPro.Contexts.Workflow.Infrastructure.Messaging;

/// <inheritdoc cref="IWorkflowCommandDispatcher"/>
/// <remarks>
/// Fase 8, Checkpoint 1.1: this is now the ONLY thing
/// <c>Workflow.Infrastructure</c> does for the
/// <see cref="CreateCleaningForReservation"/> command — the business
/// decision to send it lives in <c>Workflow.Application</c>'s
/// <c>ReservationCreatedCleaningOrchestrator</c>. <c>Send</c>, never
/// <c>Publish</c> — <see cref="CreateCleaningForReservation"/> has exactly
/// one destination Bounded Context, per ADR-018.
///
/// Fase 8, Checkpoint 2.1 (corrective audit gate): on a transport failure,
/// logs a narrow, transport-scoped entry (message type + correlation id +
/// exception) BEFORE rethrowing — deliberately not a duplicate of
/// <c>ReservationCreatedCleaningOrchestrator</c>'s own business-level audit
/// (which already captures WorkflowName/TenantId/ReservationId/Result on the
/// same failure, since the exception propagates up to it); this entry adds
/// only what Application cannot see from its own vantage point — that the
/// failure happened specifically in the SEND step. Never swallows the
/// exception — Wolverine's own redelivery/error handling remains
/// responsible for the transport-level retry behavior.
/// </remarks>
public sealed class WolverineWorkflowCommandDispatcher : IWorkflowCommandDispatcher
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<WolverineWorkflowCommandDispatcher> _logger;

    public WolverineWorkflowCommandDispatcher(IMessageBus messageBus, ILogger<WolverineWorkflowCommandDispatcher> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    public async Task DispatchCreateCleaningForReservationAsync(
        CreateCleaningForReservation command, CancellationToken cancellationToken)
    {
        try
        {
            await _messageBus.SendAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send {MessageType} (correlation {CorrelationId}) over the transport",
                nameof(CreateCleaningForReservation), command.CorrelationId);
            throw;
        }
    }
}
