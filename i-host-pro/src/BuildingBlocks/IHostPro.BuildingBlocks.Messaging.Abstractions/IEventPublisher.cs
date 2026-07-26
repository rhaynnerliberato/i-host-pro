namespace IHostPro.BuildingBlocks.Messaging.Abstractions;

/// <summary>
/// Abstraction over the concrete Event Bus (Wolverine/RabbitMQ, see ADR-004).
/// Application layers depend on this interface only — never on the messaging
/// library's types directly — so it can evolve or be replaced without touching
/// any Bounded Context's Application/Domain code.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent;
}
