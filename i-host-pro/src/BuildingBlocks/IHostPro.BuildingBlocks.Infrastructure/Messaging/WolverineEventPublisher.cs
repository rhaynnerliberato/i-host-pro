using IHostPro.BuildingBlocks.Messaging.Abstractions;
using Wolverine;

namespace IHostPro.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Sole implementation of <see cref="IEventPublisher"/>, backed by Wolverine.
/// Application layers never reference Wolverine directly — only this
/// abstraction — so the messaging library can evolve or be replaced without
/// touching any Bounded Context's Application/Domain code
/// (Architecture Principles, Section 11; ADR-004).
/// </summary>
public sealed class WolverineEventPublisher : IEventPublisher
{
    private readonly IMessageBus _messageBus;

    public WolverineEventPublisher(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        await _messageBus.PublishAsync(integrationEvent);
    }
}
