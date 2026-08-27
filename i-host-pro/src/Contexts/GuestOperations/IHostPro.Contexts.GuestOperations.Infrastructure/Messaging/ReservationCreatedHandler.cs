using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>ReservationCreated</c> (Fase 10, Checkpoint 2 —
/// Check-in/Checkout Core; choreography, ADR-016). Depends ONLY on
/// <see cref="IGuestOperationsMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>GuestOperationsDbContext</c>
/// or <c>ReservationCreatedGuestStayInitializer</c> directly.
/// </summary>
[NonTransactional]
public static class ReservationCreatedHandler
{
    public static Task Handle(
        ReservationCreated message,
        MessageContext context,
        IGuestOperationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
