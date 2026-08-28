using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Payments.Contracts;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Isolates Payments' own tenant execution, persistence and transaction
/// ownership from Wolverine's message-transport dependency graph (Fase 10,
/// Checkpoint 5 — mirrors ADR-015/016, generalized once more, exactly like
/// every other Bounded Context's own execution scope).
///
/// The single implementation of this interface is the ONLY class in
/// Payments authorized to hold an <c>IServiceScopeFactory</c>. Every
/// Wolverine transport adapter depends on this interface ONLY — never on
/// <c>PaymentsDbContext</c>, <c>IPaymentsTransactionExecutor</c>, or any
/// business handler directly.
/// </summary>
public interface IPaymentsMessageExecutionScope
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

    /// <summary>
    /// Same tenant-safe scope-opening mechanism as <see cref="ExecuteAsync{TMessage}"/>,
    /// for <see cref="PixChargeConfirmationReceived"/> instead of an
    /// <see cref="IntegrationEvent"/> — mirrors
    /// <c>IHousekeepingMessageExecutionScope.ExecuteCreateCleaningForReservationAsync</c>'s
    /// own reasoning exactly: a cross-context command/fact is not an
    /// <see cref="IntegrationEvent"/>, so it cannot flow through
    /// <see cref="ExecuteAsync{TMessage}"/>'s own generic constraint.
    /// </summary>
    Task ExecutePixChargeConfirmationReceivedAsync(
        PixChargeConfirmationReceived message, Guid messageId, CancellationToken cancellationToken);
}
