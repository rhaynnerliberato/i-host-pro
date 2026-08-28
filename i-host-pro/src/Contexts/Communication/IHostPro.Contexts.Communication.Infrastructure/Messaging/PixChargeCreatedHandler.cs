using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Payments.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="PixChargeCreated"/> (Fase 10, Checkpoint
/// 5 — PIX/Payment Deterministic Foundation). Mirrors
/// <c>LateCheckoutApprovedHandler</c>'s own shape exactly.
/// </summary>
[NonTransactional]
public static class PixChargeCreatedHandler
{
    public static Task Handle(
        PixChargeCreated message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
