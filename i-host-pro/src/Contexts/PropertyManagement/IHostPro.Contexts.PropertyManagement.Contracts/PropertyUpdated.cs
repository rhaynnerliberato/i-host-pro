using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when an existing property's code, name, capacity, condominium
/// link and/or address changes (Checkpoint 3 plan, item 11) — never for a
/// no-op update. <see cref="ChangedFields"/> names which fields changed
/// (<c>"code"</c>/<c>"name"</c>/<c>"capacity"</c>/<c>"condominium_id"</c>/
/// <c>"address"</c>, in that order), never their new values.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the updated property's id/<c>"Property"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who made the change.
/// </summary>
public sealed record PropertyUpdated : IntegrationEvent
{
    public required Guid PropertyId { get; init; }

    public required IReadOnlyCollection<string> ChangedFields { get; init; }
}
