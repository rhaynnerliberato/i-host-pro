using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Isolates Guest Operations' own tenant execution, persistence and
/// transaction ownership from Wolverine's message-transport dependency
/// graph (Fase 10, Checkpoint 2 — Check-in/Checkout Core; ADR-015/016
/// generalized a fourth time). Guest Operations becomes a real Wolverine
/// consumer for the first time this checkpoint (<c>ReservationCreated</c>,
/// via <see cref="ReservationCreatedGuestStayInitializer"/>) — the same
/// tenant-identity-divergence mechanism ADR-015 documented for Housekeeping
/// and ADR-016 generalized for Reservations/Dashboard/Communication applies
/// identically here.
///
/// The single implementation of this interface is the ONLY class in Guest
/// Operations authorized to hold an <c>IServiceScopeFactory</c> — see the
/// architecture test enforcing this boundary. The thin Wolverine adapter
/// (<c>ReservationCreatedHandler</c>) depends on this interface ONLY — never
/// on <c>GuestOperationsDbContext</c>, <c>IGuestOperationsTransactionExecutor</c>,
/// or <c>ReservationCreatedGuestStayInitializer</c> directly.
/// </summary>
public interface IGuestOperationsMessageExecutionScope
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
}
