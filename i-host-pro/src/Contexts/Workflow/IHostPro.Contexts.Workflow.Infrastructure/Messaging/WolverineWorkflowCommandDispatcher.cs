using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Workflow.Application;
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
/// </remarks>
public sealed class WolverineWorkflowCommandDispatcher : IWorkflowCommandDispatcher
{
    private readonly IMessageBus _messageBus;

    public WolverineWorkflowCommandDispatcher(IMessageBus messageBus) => _messageBus = messageBus;

    public async Task DispatchCreateCleaningForReservationAsync(
        CreateCleaningForReservation command, CancellationToken cancellationToken) =>
        await _messageBus.SendAsync(command);
}
