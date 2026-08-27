using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Housekeeping.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="LateCheckoutApproved"/> (Fase 10,
/// Checkpoint 3 — Early Check-in / Late Checkout) — mirrors
/// <see cref="ReservationCancelledHandler"/>'s own shape exactly.
/// </summary>
[NonTransactional]
public static class LateCheckoutApprovedHandler
{
    public static Task Handle(
        LateCheckoutApproved message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
