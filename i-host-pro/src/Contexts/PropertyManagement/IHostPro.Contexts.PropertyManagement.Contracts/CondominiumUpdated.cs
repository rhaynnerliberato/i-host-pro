using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when an existing condominium's name and/or address changes
/// (Checkpoint 2 plan, item 11) — never for a no-op update (Checkpoint 2
/// plan, item 7: "no-op não emite"). <see cref="ChangedFields"/> names which
/// fields changed (<c>"name"</c>/<c>"address"</c>, in that order), never
/// their new values. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the updated condominium's
/// id/<c>"Condominium"</c>. <see cref="IntegrationEvent.ActorId"/> is the
/// Administrator who made the change.
/// </summary>
public sealed record CondominiumUpdated : IntegrationEvent
{
    public required Guid CondominiumId { get; init; }

    public required IReadOnlyCollection<string> ChangedFields { get; init; }
}
