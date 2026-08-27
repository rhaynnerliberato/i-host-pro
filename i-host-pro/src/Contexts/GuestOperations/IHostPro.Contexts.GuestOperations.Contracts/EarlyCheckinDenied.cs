using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when an <c>EarlyCheckInRequest</c> is automatically denied
/// (Fase 10, Checkpoint 3) — closes the catalogue asymmetry left by
/// <see cref="EarlyCheckinApproved"/> having no denied counterpart before
/// this checkpoint. No consumer reacts to this yet — Reservation's schedule
/// never changes on a denial. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the EarlyCheckInRequest's
/// id/<c>"EarlyCheckInRequest"</c>. <see cref="ReasonCode"/> is always one of
/// <see cref="EarlyCheckinDeniedReasonCodes"/> — a known negative business
/// decision, never used for an infrastructure failure.
/// </summary>
public sealed record EarlyCheckinDenied : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required string ReasonCode { get; init; }
}
