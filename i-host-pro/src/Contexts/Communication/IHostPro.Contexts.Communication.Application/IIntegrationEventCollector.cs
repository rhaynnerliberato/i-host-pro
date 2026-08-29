using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — Communication's first
/// Integration Event collector, mirrors every other Bounded Context's own
/// exactly (e.g. <c>Payments.Application.IIntegrationEventCollector</c>).
/// </summary>
public interface IIntegrationEventCollector
{
    void Enqueue(IntegrationEvent @event);

    IReadOnlyList<IntegrationEvent> Drain();
}
