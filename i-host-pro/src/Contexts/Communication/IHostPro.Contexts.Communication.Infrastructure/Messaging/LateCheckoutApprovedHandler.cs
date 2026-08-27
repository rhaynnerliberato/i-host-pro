using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>LateCheckoutApproved</c> (Fase 10, Checkpoint 4
/// — Portaria Notification Foundation). Mirrors <see cref="ReservationCreatedHandler"/>
/// exactly. This is a THIRD, independent consumer of the same event
/// (alongside Workflow Orchestration and Housekeeping, ADR-020) — each in
/// its own sticky-bound queue, no competing consumers.
/// </summary>
[NonTransactional]
public static class LateCheckoutApprovedHandler
{
    public static Task Handle(
        LateCheckoutApproved message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
