using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when a <c>GuestStayOperation</c> transitions to CheckedIn
/// (Fase 10, Checkpoint 2 — Check-in/Checkout Core) — the guest's real
/// arrival. <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the GuestStayOperation's id/<c>"GuestStayOperation"</c>.
/// <see cref="IntegrationEvent.ActorType"/> is always <c>"System"</c> —
/// this checkpoint's endpoint has no distinct human-actor audit trail of
/// its own yet. <see cref="PropertyId"/> was added in Fase 10, Checkpoint 4
/// (Portaria Notification Foundation) — Communication's new Front Desk
/// processor needs it to resolve the current front desk contact via the new
/// synchronous exception #9 (<c>IFrontDeskContactReader</c>, ADR-026)
/// without a second lookup; it is provider-neutral and non-PII, unlike a
/// guest name/phone/access credential/instructions, which never belong here.
/// </summary>
public sealed record GuestCheckedIn : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required DateTimeOffset CheckedInAtUtc { get; init; }
}
