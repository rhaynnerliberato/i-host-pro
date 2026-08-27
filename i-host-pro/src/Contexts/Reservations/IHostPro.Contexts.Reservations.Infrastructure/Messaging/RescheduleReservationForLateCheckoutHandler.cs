using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for the cross-context command
/// <see cref="RescheduleReservationForLateCheckout"/> (Fase 10, Checkpoint
/// 3), sent exclusively by Workflow Orchestration — mirrors
/// <c>CloseReservationHandler</c> exactly.
/// </summary>
[NonTransactional]
public static class RescheduleReservationForLateCheckoutHandler
{
    public static Task Handle(
        RescheduleReservationForLateCheckout message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteRescheduleForLateCheckoutAsync(message, context.Envelope!.Id, cancellationToken);
}
