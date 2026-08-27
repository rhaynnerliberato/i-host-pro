using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when a <c>LateCheckoutRequest</c> is automatically denied
/// (Fase 10, Checkpoint 3). No consumer reacts to this yet — Reservation's
/// schedule never changes on a denial. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the LateCheckoutRequest's
/// id/<c>"LateCheckoutRequest"</c>. <see cref="ReasonCode"/> is always one of
/// <see cref="LateCheckoutDeniedReasonCodes"/> — a known negative business
/// decision, never used for an infrastructure failure.
/// </summary>
public sealed record LateCheckoutDenied : IntegrationEvent
{
    public required Guid ReservationId { get; init; }

    public required string ReasonCode { get; init; }
}
