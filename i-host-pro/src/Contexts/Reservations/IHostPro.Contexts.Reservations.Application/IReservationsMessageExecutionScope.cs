using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// Isolates Reservations' own tenant execution, persistence and transaction
/// ownership from Wolverine's message-transport dependency graph (Fase 7,
/// Checkpoint 1 — ADR-016, generalizing ADR-015's Housekeeping finding). A
/// real Wolverine message chain was observed to materialize
/// <c>ReservationsDbContext</c> with an <c>ITenantContext</c> instance
/// different from the one <c>TenantResolutionMiddleware</c> actually
/// resolved for that message, whenever the DbContext (or anything exposing
/// it as a constructor parameter — <c>IReservationsTransactionExecutor</c>,
/// <c>CleaningScheduleProjectionSynchronizer</c>) is reachable, even
/// transitively, from Wolverine's own per-message constructor-resolution
/// graph — same mechanism ADR-015 documented for Housekeeping, reproduced
/// and proven here via real generated-chain dispatch and real SQL evidence
/// (<c>WHERE FALSE</c> on <c>CleaningAssigned</c>'s projection lookup).
///
/// The single implementation of this interface is the ONLY class in
/// Reservations authorized to hold an <c>IServiceScopeFactory</c> — see
/// the architecture test enforcing this boundary. Every Wolverine transport
/// adapter (<c>CleaningCreatedHandler</c> and siblings) depends on this
/// interface ONLY — never on <c>ReservationsDbContext</c>,
/// <c>IReservationsTransactionExecutor</c>, or
/// <c>CleaningScheduleProjectionSynchronizer</c> directly — delegating
/// tenant resolution and business execution to a fresh, ordinary Microsoft
/// DI child scope that Wolverine's own codegen never sees or participates
/// in.
/// </summary>
public interface IReservationsMessageExecutionScope
{
    /// <param name="message">The real Integration Event contract instance.</param>
    /// <param name="tenantId">
    /// The canonical tenant, read by the caller directly from
    /// <paramref name="message"/>'s own <see cref="IntegrationEvent.TenantId"/>
    /// — never from the ambient <c>ITenantContext</c> that Wolverine's own
    /// per-message scope may have resolved, which is exactly the identity
    /// this boundary exists to bypass.
    /// </param>
    /// <param name="messageId">
    /// Wolverine's own envelope id (<c>Envelope.Id</c>, public API), carried
    /// through for diagnostics/correlation/future redelivery testing — never
    /// used to reconstruct or persist the envelope itself.
    /// </param>
    Task ExecuteAsync<TMessage>(
        TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        where TMessage : IntegrationEvent;

    /// <summary>
    /// Same tenant-safe scope-opening mechanism as <see cref="ExecuteAsync{TMessage}"/>,
    /// for the cross-context command <see cref="CloseReservation"/> (Fase 10,
    /// Checkpoint 1 — Guest Operations Foundation) instead of an
    /// <see cref="IntegrationEvent"/> — mirrors Housekeeping's own
    /// <c>IHousekeepingMessageExecutionScope.ExecuteCreateCleaningForReservationAsync</c>
    /// exactly, including its own reasoning for staying narrowly typed rather
    /// than a generic command-bus method.
    /// </summary>
    Task ExecuteCloseReservationAsync(
        CloseReservation command, Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Same tenant-safe scope-opening mechanism, for the cross-context
    /// command <see cref="RescheduleReservationForEarlyCheckIn"/> (Fase 10,
    /// Checkpoint 3 — Early Check-in/Late Checkout).
    /// </summary>
    Task ExecuteRescheduleForEarlyCheckInAsync(
        RescheduleReservationForEarlyCheckIn command, Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Same tenant-safe scope-opening mechanism, for the cross-context
    /// command <see cref="RescheduleReservationForLateCheckout"/> (Fase 10,
    /// Checkpoint 3 — Early Check-in/Late Checkout).
    /// </summary>
    Task ExecuteRescheduleForLateCheckoutAsync(
        RescheduleReservationForLateCheckout command, Guid messageId, CancellationToken cancellationToken);
}
