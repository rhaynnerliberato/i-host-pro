using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>InboundGuestMessageReceived</c> (Fase 11,
/// Checkpoint 1 — Inbound Conversation Foundation). Depends ONLY on
/// <see cref="ICommunicationMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>CommunicationDbContext</c> or
/// the message processor directly. Mirrors <c>ReservationCreatedHandler</c>
/// exactly.
/// </summary>
[NonTransactional]
public static class InboundGuestMessageReceivedHandler
{
    public static Task Handle(
        InboundGuestMessageReceived message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
