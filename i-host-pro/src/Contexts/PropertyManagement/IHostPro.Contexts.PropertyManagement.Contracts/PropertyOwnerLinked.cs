using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// Published when a user is linked as a property's owner (Checkpoint 5 plan,
/// item 15) — never on a rejected/conflicting attempt.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the property's id/<c>"Property"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who created the link. <see cref="IntegrationEvent.TenantId"/>
/// (base envelope) already carries the tenant — no separate field here.
/// Carries no owner name/email/role — never personal data.
/// </summary>
public sealed record PropertyOwnerLinked : IntegrationEvent
{
    public required Guid PropertyId { get; init; }

    public required Guid OwnerUserId { get; init; }
}
