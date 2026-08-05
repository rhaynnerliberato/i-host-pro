using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Reservations.Application;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IIntegrationEventCollector"/>
/// <remarks>Plain in-memory list — registered Scoped, one instance per request — mirrors Identity's/Property Management's own.</remarks>
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
