using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when a property transitions to <c>Inactive</c> (Checkpoint 4
/// plan, item 11) — never on a rejected/conflicting attempt. Same envelope
/// convention as <see cref="PropertyActivated"/>.
/// </summary>
public sealed record PropertyDeactivated : IntegrationEvent
{
    public required Guid PropertyId { get; init; }
}
