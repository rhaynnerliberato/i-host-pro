using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when an owner's link to a property is removed (Checkpoint 5
/// plan, item 15) — never on a rejected/repeated attempt. Same envelope
/// convention as <see cref="PropertyOwnerLinked"/>.
/// </summary>
public sealed record PropertyOwnerUnlinked : IntegrationEvent
{
    public required Guid PropertyId { get; init; }

    public required Guid OwnerUserId { get; init; }
}
