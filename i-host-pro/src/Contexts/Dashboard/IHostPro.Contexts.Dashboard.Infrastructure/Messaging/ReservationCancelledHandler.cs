using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Messaging;

/// <summary>Wolverine adapter for <c>ReservationCancelled</c> — see <c>ReservationCreatedHandler</c>'s own doc comment.</summary>
[NonTransactional]
public static class ReservationCancelledHandler
{
    public static Task Handle(
        ReservationCancelled message,
        MessageContext context,
        IDashboardMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
