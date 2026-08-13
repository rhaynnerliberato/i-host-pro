using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>CleaningAssigned</c> (Fase 7, Checkpoint 1
/// CLOSURE — ADR-016, generalizing ADR-015's Housekeeping finding). Depends
/// ONLY on <see cref="IReservationsMessageExecutionScope"/> and Wolverine's
/// own <see cref="MessageContext"/> (for the envelope's own id) — never on
/// <c>ReservationsDbContext</c>, <c>IReservationsTransactionExecutor</c>, or
/// <c>CleaningScheduleProjectionSynchronizer</c> directly, so none of those
/// types are ever reachable from Wolverine's own handler-chain dependency
/// graph — that reachability is exactly what caused the tenant-identity
/// divergence (real SQL evidence: <c>WHERE FALSE</c> on this event's
/// projection lookup, root-caused and fixed in this checkpoint).
///
/// Migrated first, alone, as the spike proving the boundary fixes the real
/// defect before generalizing to the other nine Cleaning lifecycle
/// adapters — see ADR-016 and the Fase 7 homologação document for the full
/// investigation narrative.
/// <see cref="NonTransactionalAttribute"/> documents intent (Reservations
/// owns its own transaction explicitly, via the execution scope) but is
/// not, by itself, what makes this safe — the absence of any
/// DbContext-reachable type in this chain is.
/// </summary>
[NonTransactional]
public static class CleaningAssignedHandler
{
    public static Task Handle(
        CleaningAssigned message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
