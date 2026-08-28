using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Payments.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="PixChargeConfirmed"/> (Fase 10,
/// Checkpoint 5 — PIX/Payment Deterministic Foundation; choreography, async
/// boundary). Depends ONLY on <see cref="IGuestOperationsMessageExecutionScope"/>
/// and Wolverine's own <see cref="MessageContext"/> — mirrors
/// <c>ReservationCreatedHandler</c> exactly.
/// </summary>
[NonTransactional]
public static class PixChargeConfirmedHandler
{
    public static Task Handle(
        PixChargeConfirmed message,
        MessageContext context,
        IGuestOperationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
