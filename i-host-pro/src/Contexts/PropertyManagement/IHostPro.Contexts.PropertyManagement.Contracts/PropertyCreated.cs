using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when a new property is created (Checkpoint 3 plan, item 11).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the new property's id/<c>"Property"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who created it. <see cref="Status"/> is the stable
/// code (always <c>"draft"</c> at creation — Checkpoint 3 plan, item 3),
/// never the raw enum member name. Carries no code/name/capacity/address/
/// condominiumId — never personal or business-sensitive content.
/// </summary>
public sealed record PropertyCreated : IntegrationEvent
{
    public required Guid PropertyId { get; init; }

    public required string Status { get; init; }
}
