using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Application;

/// <summary>
/// Isolates Housekeeping's own tenant execution, persistence and
/// transaction ownership from Wolverine's message-transport dependency
/// graph (Fase 6, Checkpoint 6 — ADR-015). A real Wolverine message chain
/// was observed to materialize <c>HousekeepingDbContext</c> with an
/// <c>ITenantContext</c> instance different from the one
/// <c>TenantResolutionMiddleware</c> actually resolved for that message,
/// whenever the DbContext (or anything exposing it as a constructor
/// parameter — <c>IHousekeepingTransactionExecutor</c>,
/// <c>IHousekeepingAuditWriter</c>, a business processor) is reachable, even
/// transitively, from Wolverine's own per-message constructor-resolution
/// graph — confirmed not fixable by three separate, officially-documented
/// Wolverine EF Core integration patterns (see ADR-015's own record of the
/// three refuted experiments).
///
/// The single implementation of this interface is the ONLY class in
/// Housekeeping authorized to hold an <c>IServiceScopeFactory</c> — see
/// the architecture test enforcing this boundary. Every Wolverine transport
/// adapter (<c>ReservationCancelledHandler</c> and siblings) depends on this
/// interface ONLY — never on <c>HousekeepingDbContext</c>,
/// <c>IHousekeepingTransactionExecutor</c>, or any business processor
/// directly — delegating tenant resolution and business execution to a
/// fresh, ordinary Microsoft DI child scope that Wolverine's own codegen
/// never sees or participates in.
/// </summary>
public interface IHousekeepingMessageExecutionScope
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
