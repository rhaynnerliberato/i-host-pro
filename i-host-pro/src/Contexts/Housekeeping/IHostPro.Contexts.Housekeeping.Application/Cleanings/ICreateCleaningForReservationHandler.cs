using IHostPro.Contexts.Housekeeping.Contracts;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Handles the cross-context command <see cref="CreateCleaningForReservation"/>
/// (Fase 8, Checkpoint 1 — ADR-018), sent exclusively by Workflow
/// Orchestration. Deliberately NOT modeled through the Mediator
/// <c>ICommandHandler&lt;,&gt;</c> pipeline this context's HTTP-facing
/// commands use — that pipeline (validators, <c>TenantTransactionBehavior</c>)
/// is registered ONLY in <c>IHostPro.Api</c>'s composition root
/// (<c>HousekeepingCommandDispatchExtensions</c>'s own doc comment), never
/// <c>IHostPro.Worker</c>'s, and this command is consumed exclusively in
/// the Worker. A small, dedicated interface avoids introducing the entire
/// Mediator/FluentValidation stack into the Worker just for this one
/// command.
/// </summary>
public interface ICreateCleaningForReservationHandler
{
    Task HandleAsync(CreateCleaningForReservation command, CancellationToken cancellationToken);
}
