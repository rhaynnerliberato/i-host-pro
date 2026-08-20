using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.ExternalIntegrations.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>Registered Singleton in tests (unlike the real Scoped registration) so a single instance observes every child-scope publish across the whole test.</summary>
internal sealed class RecordingIntegrationEventCollector : IIntegrationEventCollector
{
    public List<IntegrationEvent> Enqueued { get; } = [];

    public void Enqueue(IntegrationEvent @event) => Enqueued.Add(@event);

    public IReadOnlyList<IntegrationEvent> Drain()
    {
        var drained = Enqueued.ToArray();
        Enqueued.Clear();
        return drained;
    }
}
