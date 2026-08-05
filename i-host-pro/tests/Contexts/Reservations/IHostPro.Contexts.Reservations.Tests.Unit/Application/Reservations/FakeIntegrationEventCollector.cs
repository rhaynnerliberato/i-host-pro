using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Reservations.Application;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

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
