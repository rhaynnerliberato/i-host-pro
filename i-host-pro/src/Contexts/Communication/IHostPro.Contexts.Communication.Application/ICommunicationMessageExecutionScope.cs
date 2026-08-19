using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Isolates Communication's own tenant execution, persistence and
/// transaction ownership from Wolverine's message-transport dependency
/// graph — the local, per-Bounded-Context boundary ADR-016 requires for ANY
/// persistent Wolverine consumer that touches a <c>BaseDbContext</c>-derived,
/// tenant-filtered <c>DbContext</c> (mirrors
/// <c>IDashboardMessageExecutionScope</c>/<c>IHousekeepingMessageExecutionScope</c>/
/// <c>IReservationsMessageExecutionScope</c> exactly — ADR-016's own
/// "Alternativa Rejeitada 2" already establishes this is deliberately
/// duplicated per context, never a shared generic abstraction).
///
/// The single implementation of this interface is the ONLY class in
/// Communication authorized to hold an <c>IServiceScopeFactory</c> — see the
/// architecture test enforcing this boundary. The Wolverine transport
/// adapter depends on this interface ONLY — never on
/// <c>CommunicationDbContext</c> or the message processor directly.
/// </summary>
public interface ICommunicationMessageExecutionScope
{
    /// <param name="message">The real Integration Event contract instance.</param>
    /// <param name="tenantId">
    /// The canonical tenant, read by the caller directly from
    /// <paramref name="message"/>'s own <see cref="IntegrationEvent.TenantId"/>
    /// — never from the ambient <c>ITenantContext</c> that Wolverine's own
    /// per-message scope may have resolved.
    /// </param>
    /// <param name="messageId">Wolverine's own envelope id, carried through for diagnostics/correlation only.</param>
    Task ExecuteAsync<TMessage>(
        TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        where TMessage : IntegrationEvent;
}
