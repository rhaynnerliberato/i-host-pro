using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Dashboard.Application;

/// <summary>
/// Isolates Dashboard &amp; Reporting's own tenant execution, persistence and
/// transaction ownership from Wolverine's message-transport dependency
/// graph — the local, per-Bounded-Context boundary ADR-016 requires for
/// ANY persistent Wolverine consumer that touches a <c>BaseDbContext</c>-
/// derived, tenant-filtered <c>DbContext</c> (ADR-016 already states this as
/// a general rule; this is not a new architectural decision, only its third
/// application — see ADR-016's own "Alternativa Rejeitada 2": a shared
/// abstraction across Housekeeping/Reservations/Dashboard was deliberately
/// NOT adopted this checkpoint, to avoid refactoring the two existing
/// Bounded Contexts mid-feature — same design as
/// <c>IHousekeepingMessageExecutionScope</c>/<c>IReservationsMessageExecutionScope</c>,
/// deliberately duplicated rather than shared).
///
/// The single implementation of this interface is the ONLY class in
/// Dashboard authorized to hold an <c>IServiceScopeFactory</c> — see the
/// architecture test enforcing this boundary. Every Wolverine transport
/// adapter depends on this interface ONLY — never on
/// <c>DashboardDbContext</c> or any projection synchronizer directly —
/// delegating tenant resolution and business execution to a fresh, ordinary
/// Microsoft DI child scope that Wolverine's own codegen never sees or
/// participates in.
/// </summary>
public interface IDashboardMessageExecutionScope
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
    /// through for diagnostics/correlation — never used to reconstruct or
    /// persist the envelope itself.
    /// </param>
    Task ExecuteAsync<TMessage>(
        TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        where TMessage : IntegrationEvent;
}
