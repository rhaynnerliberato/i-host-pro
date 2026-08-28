using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Payments.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="PixChargeExpirationReceived"/> (Fase 10,
/// Checkpoint 5.1 — Payment Failure/Expiration Evidence Corrective Gate).
/// Mirrors <c>PixChargeConfirmationReceivedHandler</c>'s own shape exactly.
/// </summary>
[NonTransactional]
public static class PixChargeExpirationReceivedHandler
{
    public static Task Handle(
        PixChargeExpirationReceived message,
        MessageContext context,
        IPaymentsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecutePixChargeExpirationReceivedAsync(message, context.Envelope!.Id, cancellationToken);
}
