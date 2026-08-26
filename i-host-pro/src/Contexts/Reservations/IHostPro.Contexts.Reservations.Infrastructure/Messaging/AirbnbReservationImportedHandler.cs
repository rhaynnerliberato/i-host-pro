using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="AirbnbReservationImported"/> (Fase 9,
/// Checkpoint 3.2) — mirrors <c>CleaningCreatedHandler</c> exactly: depends
/// ONLY on <see cref="IReservationsMessageExecutionScope"/> and Wolverine's
/// own <see cref="MessageContext"/>, never on <c>ReservationsDbContext</c>/
/// <c>IReservationsTransactionExecutor</c> directly (ADR-016).
/// </summary>
[NonTransactional]
public static class AirbnbReservationImportedHandler
{
    public static Task Handle(
        AirbnbReservationImported message,
        MessageContext context,
        IReservationsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
