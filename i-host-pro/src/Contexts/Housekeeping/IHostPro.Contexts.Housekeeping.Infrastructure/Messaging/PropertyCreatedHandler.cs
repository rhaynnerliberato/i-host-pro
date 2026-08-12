using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.PropertyManagement.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// The minimal, mechanical adapter the messaging library (Wolverine)
/// discovers by its own naming convention (class name ending in
/// <c>Handler</c>, public static method named <c>Handle</c>) and invokes
/// for every consumed <c>PropertyCreated</c> message (Fase 6, Checkpoint 6
/// — ADR-015). Depends ONLY on <see cref="IHousekeepingMessageExecutionScope"/>
/// and Wolverine's own <see cref="MessageContext"/> — never on
/// <c>HousekeepingDbContext</c> or <c>PropertyProjectionSynchronizer</c>
/// directly, so neither is ever reachable from Wolverine's own handler-chain
/// dependency graph. See <c>ReservationCancelledHandler</c>'s own doc
/// comment for the full rationale.
/// </summary>
[NonTransactional]
public static class PropertyCreatedHandler
{
    public static Task Handle(
        PropertyCreated message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
