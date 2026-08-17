using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Dashboard.Application;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence;

/// <inheritdoc cref="IIntegrationEventCollector"/>
/// <remarks>Plain in-memory list — registered Scoped, one instance per message — mirrors every other context's own (deliberately duplicated, not shared).</remarks>
public sealed class IntegrationEventCollector : IIntegrationEventCollector
{
    private readonly List<IntegrationEvent> _events = [];

    public void Enqueue(IntegrationEvent @event) => _events.Add(@event);

    public IReadOnlyList<IntegrationEvent> Drain()
    {
        var drained = _events.ToArray();
        _events.Clear();
        return drained;
    }
}
