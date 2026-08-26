using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// The cross-context command handler contract for <see cref="CloseReservation"/>
/// (Fase 10, Checkpoint 1 — Guest Operations Foundation) — mirrors
/// Housekeeping's own <c>ICreateCleaningForReservationHandler</c> exactly.
/// Deliberately NOT a second generic command-bus abstraction — ADR-018
/// already rejects a generic command bus; this stays narrowly typed to the
/// one command Reservations receives from Workflow Orchestration.
/// </summary>
public interface ICloseReservationHandler
{
    Task HandleAsync(CloseReservation command, CancellationToken cancellationToken);
}
