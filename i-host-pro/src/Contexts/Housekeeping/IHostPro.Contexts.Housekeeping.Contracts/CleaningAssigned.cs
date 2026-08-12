using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a Cleaning is assigned to a Housekeeper (Fase 6,
/// Incremento 1) — <c>Pending</c> → <c>Assigned</c>. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the Cleaning's
/// id/<c>"Cleaning"</c>. <see cref="IntegrationEvent.ActorId"/> is the
/// Administrator/Operator who made the assignment — never the assignee.
/// </summary>
public sealed record CleaningAssigned : IntegrationEvent
{
    public required Guid CleaningId { get; init; }

    public required Guid HousekeeperUserId { get; init; }
}
