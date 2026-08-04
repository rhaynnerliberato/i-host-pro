using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when a property transitions to <c>Archived</c> (Checkpoint 4
/// plan, item 11) — terminal in this increment, never on a
/// rejected/conflicting attempt. Same envelope convention as
/// <see cref="PropertyActivated"/>.
/// </summary>
public sealed record PropertyArchived : IntegrationEvent
{
    public required Guid PropertyId { get; init; }
}
