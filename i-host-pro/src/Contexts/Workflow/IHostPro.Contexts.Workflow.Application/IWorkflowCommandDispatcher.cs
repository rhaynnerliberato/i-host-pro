using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Workflow.Application;

/// <summary>
/// The minimal transport abstraction Workflow Orchestration's Application
/// layer needs to send a cross-context command, without depending on
/// Wolverine (Fase 8, Checkpoint 1.1 — corrects the CP1 layering blocker:
/// the orchestration use case previously lived directly in
/// <c>Workflow.Infrastructure</c>, mixing the business decision with its own
/// transport). Deliberately NOT a generic <c>IWorkflowCommandBus</c>/
/// <c>ICommandDispatcher&lt;T&gt;</c> — ADR-018 already rejects a generic
/// command bus for this checkpoint; this stays narrowly typed to the one
/// command Workflow currently sends, exactly like
/// <c>IHousekeepingMessageExecutionScope.ExecuteCreateCleaningForReservationAsync</c>
/// on the receiving side. Extended again, narrowly, if and when a second
/// cross-context command exists — never generalized ahead of that need.
///
/// Implemented in <c>Workflow.Infrastructure</c> via Wolverine's
/// <c>IMessageBus.SendAsync</c> — the only thing that project is now
/// responsible for regarding this command.
/// </summary>
public interface IWorkflowCommandDispatcher
{
    Task DispatchCreateCleaningForReservationAsync(
        CreateCleaningForReservation command, CancellationToken cancellationToken);

    /// <summary>
    /// Sends Reservations' own cross-context command <see cref="CloseReservation"/>
    /// (Fase 10, Checkpoint 1 — Guest Operations Foundation) — the second
    /// command this dispatcher sends, added narrowly rather than
    /// generalized into an open command-bus method, mirroring
    /// <see cref="DispatchCreateCleaningForReservationAsync"/>'s own
    /// precedent exactly.
    /// </summary>
    Task DispatchCloseReservationAsync(
        CloseReservation command, CancellationToken cancellationToken);
}
