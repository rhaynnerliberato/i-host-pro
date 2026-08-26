using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Published by External Integrations when an already-imported Airbnb
/// reservation is cancelled (Fase 9, Checkpoint 3.2). Carries no guest PII —
/// only the identity needed to look the reservation up (CP3.2 mandate §7).
/// The consumer calls <c>Reservation.Cancel</c> when the looked-up
/// reservation is still <c>Confirmed</c>; an already-<c>Cancelled</c>
/// reservation is a no-op (never re-thrown), and an unknown
/// <see cref="ExternalReservationId"/> creates nothing (CP3.2 mandate §22).
/// </summary>
public sealed record AirbnbReservationCancelled : IntegrationEvent
{
    public required string ExternalReservationId { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
