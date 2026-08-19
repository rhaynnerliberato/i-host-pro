using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>ReservationCreated</c> (Fase 9, Checkpoint 1 —
/// choreography, ADR-016). Depends ONLY on
/// <see cref="ICommunicationMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>CommunicationDbContext</c> or
/// the message processor directly.
/// </summary>
[NonTransactional]
public static class ReservationCreatedHandler
{
    public static Task Handle(
        ReservationCreated message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
