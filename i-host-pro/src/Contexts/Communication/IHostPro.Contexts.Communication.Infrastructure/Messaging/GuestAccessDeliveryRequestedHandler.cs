using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="GuestAccessDeliveryRequested"/> (Fase
/// 10, Checkpoint 6.2 — Guest Access Secure Delivery Corrective
/// Implementation). Mirrors <c>PixChargeCreatedHandler</c>'s own shape
/// exactly.
/// </summary>
[NonTransactional]
public static class GuestAccessDeliveryRequestedHandler
{
    public static Task Handle(
        GuestAccessDeliveryRequested message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
