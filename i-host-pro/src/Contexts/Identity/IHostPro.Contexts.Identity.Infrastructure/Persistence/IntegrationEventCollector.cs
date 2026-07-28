using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IIntegrationEventCollector"/>
/// <remarks>
/// Plain in-memory list — registered Scoped, one instance per request, never
/// accessed concurrently within that scope (mirrors <see cref="SessionRevocationSignal"/>).
/// </remarks>
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
