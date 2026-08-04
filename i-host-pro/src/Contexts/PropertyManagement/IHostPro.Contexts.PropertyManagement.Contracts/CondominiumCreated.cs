using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when a new condominium is created (Checkpoint 2 plan, item 11).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the new condominium's id/<c>"Condominium"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who created it. Carries no name/address — never
/// personal or business-sensitive content (Checkpoint 2 plan, item 11).
/// </summary>
public sealed record CondominiumCreated : IntegrationEvent
{
    public required Guid CondominiumId { get; init; }
}
