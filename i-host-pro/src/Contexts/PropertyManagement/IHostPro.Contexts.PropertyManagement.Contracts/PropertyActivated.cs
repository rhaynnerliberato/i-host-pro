using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when a property transitions to <c>Active</c> (Checkpoint 4
/// plan, item 11) — never on a rejected/conflicting attempt.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the property's id/<c>"Property"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who activated it. Carries no code/name/capacity/
/// address/condominiumId — never business-sensitive content.
/// </summary>
public sealed record PropertyActivated : IntegrationEvent
{
    public required Guid PropertyId { get; init; }
}
