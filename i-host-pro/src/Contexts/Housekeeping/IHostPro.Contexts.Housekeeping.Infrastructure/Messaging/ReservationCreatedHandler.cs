using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>ReservationCreated</c> (Fase 6, Checkpoint 6 —
/// ADR-015). Depends ONLY on <see cref="IHousekeepingMessageExecutionScope"/>
/// and Wolverine's own <see cref="MessageContext"/> — see
/// <c>ReservationCancelledHandler</c>'s own doc comment for the full
/// rationale (this is the same official pattern, generalized after that
/// spike gate was proven green).
/// </summary>
[NonTransactional]
public static class ReservationCreatedHandler
{
    public static Task Handle(
        ReservationCreated message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
