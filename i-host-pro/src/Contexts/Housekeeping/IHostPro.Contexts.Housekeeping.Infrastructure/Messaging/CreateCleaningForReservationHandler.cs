using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for the cross-context command
/// <see cref="CreateCleaningForReservation"/> (Fase 8, Checkpoint 1 —
/// ADR-018), sent exclusively by Workflow Orchestration. Depends ONLY on
/// <see cref="IHousekeepingMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — same thin-adapter shape as
/// <c>ReservationCreatedHandler</c> and every other Wolverine adapter in
/// this context; never resolves <c>HousekeepingDbContext</c> or any
/// business processor directly.
/// </summary>
[NonTransactional]
public static class CreateCleaningForReservationHandler
{
    public static Task Handle(
        CreateCleaningForReservation message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteCreateCleaningForReservationAsync(message, context.Envelope!.Id, cancellationToken);
}
