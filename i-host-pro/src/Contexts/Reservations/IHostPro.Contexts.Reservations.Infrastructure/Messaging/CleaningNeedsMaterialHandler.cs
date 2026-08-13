using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <inheritdoc cref="CleaningAssignedHandler"/>
[NonTransactional]
public static class CleaningNeedsMaterialHandler
{
    public static Task Handle(
        CleaningNeedsMaterial message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
