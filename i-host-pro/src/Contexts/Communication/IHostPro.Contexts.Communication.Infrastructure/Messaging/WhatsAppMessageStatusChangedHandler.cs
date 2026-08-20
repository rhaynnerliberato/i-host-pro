using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>WhatsAppMessageStatusChanged</c> (Fase 9,
/// Checkpoint 2.3.3, ADR-022 item 14). Depends ONLY on
/// <see cref="ICommunicationMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>CommunicationDbContext</c> or
/// the message processor directly. Mirrors <see cref="ReservationCreatedHandler"/>
/// exactly.
/// </summary>
[NonTransactional]
public static class WhatsAppMessageStatusChangedHandler
{
    public static Task Handle(
        WhatsAppMessageStatusChanged message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
