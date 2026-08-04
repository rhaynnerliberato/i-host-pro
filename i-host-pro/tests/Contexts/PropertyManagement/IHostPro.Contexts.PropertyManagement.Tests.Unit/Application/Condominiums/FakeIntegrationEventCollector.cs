using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.PropertyManagement.Application;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeIntegrationEventCollector : IIntegrationEventCollector
{
    public List<IntegrationEvent> EnqueuedEvents { get; } = [];

    public void Enqueue(IntegrationEvent @event) => EnqueuedEvents.Add(@event);

    public IReadOnlyList<IntegrationEvent> Drain()
    {
        var drained = EnqueuedEvents.ToArray();
        EnqueuedEvents.Clear();
        return drained;
    }
}
