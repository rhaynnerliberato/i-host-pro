using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for the cross-context command
/// <see cref="CloseReservation"/> (Fase 10, Checkpoint 1 — Guest Operations
/// Foundation), sent exclusively by Workflow Orchestration. Depends ONLY on
/// <see cref="IReservationsMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — same thin-adapter shape as every other
/// Wolverine adapter in this context; never resolves
/// <c>ReservationsDbContext</c> or any business processor directly. Mirrors
/// Housekeeping's own <c>CreateCleaningForReservationHandler</c> exactly.
/// </summary>
[NonTransactional]
public static class CloseReservationHandler
{
    public static Task Handle(
        CloseReservation message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteCloseReservationAsync(message, context.Envelope!.Id, cancellationToken);
}
