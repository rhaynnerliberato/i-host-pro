using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>ReservationCancelled</c> (Fase 6, Checkpoint 6
/// — ADR-015). Depends ONLY on <see cref="IHousekeepingMessageExecutionScope"/>
/// and Wolverine's own <see cref="MessageContext"/> (for the envelope's own
/// id) — never on <c>HousekeepingDbContext</c>,
/// <c>IHousekeepingTransactionExecutor</c>, or
/// <see cref="IHostPro.BuildingBlocks.Application.IIntegrationEventHandler{TEvent}"/>
/// directly, so none of those types are ever reachable from Wolverine's own
/// handler-chain dependency graph — that reachability is exactly what
/// caused the tenant-identity divergence ADR-015 documents.
/// <see cref="NonTransactionalAttribute"/> documents intent (Housekeeping
/// owns its own transaction explicitly) but is not, by itself, what makes
/// this safe — the absence of any DbContext-reachable type in this chain is.
/// </summary>
[NonTransactional]
public static class ReservationCancelledHandler
{
    public static Task Handle(
        ReservationCancelled message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
