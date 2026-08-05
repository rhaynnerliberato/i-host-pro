using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// Per-request (Scoped) collector of Integration Events staged by a handler
/// while inside its transaction — own copy of
/// <c>PropertyManagement.Application.IIntegrationEventCollector</c>'s shape
/// (Architecture Principles §4: Application layers never reference each
/// other, so this small abstraction is intentionally duplicated per Bounded
/// Context rather than shared). <c>ReservationsOutboxTransactionExecutor</c>
/// drains this immediately before flushing the transactional outbox.
/// </summary>
public interface IIntegrationEventCollector
{
    void Enqueue(IntegrationEvent @event);

    /// <summary>Returns every event staged since the last <see cref="Drain"/> call, and clears the collector.</summary>
    IReadOnlyList<IntegrationEvent> Drain();
}
