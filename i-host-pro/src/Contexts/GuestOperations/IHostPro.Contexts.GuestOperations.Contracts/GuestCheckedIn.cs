using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when a <c>GuestStayOperation</c> transitions to CheckedIn
/// (Fase 10, Checkpoint 2 — Check-in/Checkout Core) — the guest's real
/// arrival. <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the GuestStayOperation's id/<c>"GuestStayOperation"</c>.
/// <see cref="IntegrationEvent.ActorType"/> is always <c>"System"</c> —
/// this checkpoint's endpoint has no distinct human-actor audit trail of
/// its own yet. Payload carries only <see cref="ReservationId"/>/
/// <see cref="CheckedInAtUtc"/> — no PropertyId (no concrete consumer needs
/// it), no guest name/phone/access credential/instructions — never
/// business-sensitive content.
/// </summary>
public sealed record GuestCheckedIn : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required DateTimeOffset CheckedInAtUtc { get; init; }
}
