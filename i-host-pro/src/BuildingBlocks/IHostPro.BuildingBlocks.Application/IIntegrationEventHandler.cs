using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// The only abstraction a Bounded Context implements to react to a consumed
/// Integration Event. Contains no reference to any messaging library — a
/// deliberate isolation boundary so the plaform's messaging backbone can be
/// replaced without touching business modules (Architecture Principles,
/// Section 11; ADR-004). Each module's Infrastructure layer provides a small,
/// mechanical adapter that the messaging library discovers and that simply
/// resolves and invokes this interface from the DI container.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
