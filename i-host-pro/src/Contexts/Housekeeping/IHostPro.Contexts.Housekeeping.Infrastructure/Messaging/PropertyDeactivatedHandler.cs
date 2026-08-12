using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.PropertyManagement.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>PropertyDeactivated</c> — see
/// <see cref="PropertyCreatedHandler"/>'s own doc comment.
/// </summary>
[NonTransactional]
public static class PropertyDeactivatedHandler
{
    public static Task Handle(
        PropertyDeactivated message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
