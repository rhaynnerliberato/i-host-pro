using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <summary>Wolverine adapter for <see cref="AirbnbReservationCancelled"/> (Fase 9, Checkpoint 3.2) — mirrors <c>AirbnbReservationImportedHandler</c> exactly.</summary>
[NonTransactional]
public static class AirbnbReservationCancelledHandler
{
    public static Task Handle(
        AirbnbReservationCancelled message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
