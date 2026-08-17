using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Messaging;

/// <summary>Wolverine adapter for <c>CleaningAssigned</c> — see <c>CleaningCreatedHandler</c>'s own doc comment.</summary>
[NonTransactional]
public static class CleaningAssignedHandler
{
    public static Task Handle(
        CleaningAssigned message,
        MessageContext context,
        IDashboardMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
