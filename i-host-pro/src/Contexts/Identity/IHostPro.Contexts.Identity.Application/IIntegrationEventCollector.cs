using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Per-request (Scoped) collector of Integration Events staged by a handler
/// while inside its transaction (Incremento 2 plan, Etapa 15A) — the same
/// shape as <see cref="ISessionRevocationSignal"/>, deliberately: it lets a
/// handler register an event to publish without depending on Wolverine or on
/// any transactional/outbox mechanism (only <c>BuildingBlocks.Messaging.Abstractions</c>,
/// which every Bounded Context Application layer is already allowed to
/// reference). <c>IdentityOutboxTransactionExecutor</c> drains this
/// immediately before flushing the transactional outbox, and clears it (by
/// draining and discarding the result) both at the start of an attempt and
/// whenever an attempt is retried or fails, so a reverted attempt's events
/// never reach the next attempt or an aborted transaction.
/// </summary>
public interface IIntegrationEventCollector
{
    void Enqueue(IntegrationEvent @event);

    /// <summary>Returns every event staged since the last <see cref="Drain"/> call, and clears the collector.</summary>
    IReadOnlyList<IntegrationEvent> Drain();
}
