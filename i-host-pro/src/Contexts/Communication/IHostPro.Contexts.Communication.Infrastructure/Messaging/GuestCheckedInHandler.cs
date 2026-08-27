using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>GuestCheckedIn</c> (Fase 10, Checkpoint 4 —
/// Portaria Notification Foundation). Mirrors <see cref="ReservationCreatedHandler"/>
/// exactly — depends ONLY on <see cref="ICommunicationMessageExecutionScope"/>
/// and Wolverine's own <see cref="MessageContext"/>.
/// </summary>
[NonTransactional]
public static class GuestCheckedInHandler
{
    public static Task Handle(
        GuestCheckedIn message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
