using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>CleaningCreated</c> (Fase 7, Checkpoint 1
/// CLOSURE — ADR-016, generalizing ADR-015's Housekeeping finding). Depends
/// ONLY on <see cref="IReservationsMessageExecutionScope"/> and Wolverine's
/// own <see cref="MessageContext"/> (for the envelope's own id) — never on
/// <c>ReservationsDbContext</c>, <c>IReservationsTransactionExecutor</c>, or
/// <c>CleaningScheduleProjectionSynchronizer</c> directly, so none of those
/// types are ever reachable from Wolverine's own handler-chain dependency
/// graph — that reachability is exactly what caused the tenant-identity
/// divergence.
///
/// Migrated second, immediately after <c>CleaningAssignedHandler</c>'s spike
/// proved the boundary — necessary here specifically because
/// <c>CleaningScheduleProjectionSynchronizer.HandleAsync(CleaningCreated, ...)</c>'s
/// own idempotency guard (<c>AnyAsync(...)</c>, an EF query, therefore
/// subject to the same Global Query Filter defect) was silently returning
/// <c>false</c> for every redelivery — a real, previously-undiscovered
/// consequence of the same root cause, masked because the guard's only
/// effect before this fix was allowing a harmless-looking duplicate
/// <c>INSERT</c> attempt (which would have thrown on the table's composite
/// primary key on an actual redelivery).
/// <see cref="NonTransactionalAttribute"/> documents intent (Reservations
/// owns its own transaction explicitly, via the execution scope) but is
/// not, by itself, what makes this safe — the absence of any
/// DbContext-reachable type in this chain is.
/// </summary>
[NonTransactional]
public static class CleaningCreatedHandler
{
    public static Task Handle(
        CleaningCreated message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
