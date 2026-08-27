using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>EarlyCheckinApproved</c> (Fase 10, Checkpoint 4
/// — Portaria Notification Foundation). Mirrors <see cref="ReservationCreatedHandler"/>
/// exactly. This is a second, independent consumer of the same event
/// Workflow Orchestration already reacts to (ADR-020) — each in its own
/// sticky-bound queue.
/// </summary>
[NonTransactional]
public static class EarlyCheckinApprovedHandler
{
    public static Task Handle(
        EarlyCheckinApproved message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
