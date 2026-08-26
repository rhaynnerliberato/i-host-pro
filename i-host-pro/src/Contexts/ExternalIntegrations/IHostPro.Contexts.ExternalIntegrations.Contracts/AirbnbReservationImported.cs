using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Published by External Integrations when an Airbnb reservation should be
/// materialized as a real <c>Reservation</c> (Fase 9, Checkpoint 3.2 —
/// "Airbnb Deterministic Foundation"; CP3.1 Decision Gate item 12, Option A).
/// <see cref="PropertyId"/> is the INTERNAL property id — External
/// Integrations resolves <c>ExternalListingId → PropertyId</c> via its own
/// <c>AirbnbListingMapping</c> BEFORE publishing this event, so Reservations
/// never needs to know the external listing id or anything about the
/// mapping (CP3.2 mandate §3).
///
/// Carries exactly the fields <c>Reservation.CreateImported</c> already
/// requires — never more (no email/phone/reviews/message content/pricing/
/// raw provider payload, CP3.2 mandate §2/§5). Never a new synchronous
/// cross-context exception: Reservations consumes this exactly like it
/// already consumes Housekeeping's <c>Cleaning*</c> events, ordinary
/// decoupled pub/sub (CP3.2 mandate §13/§15).
/// </summary>
public sealed record AirbnbReservationImported : IntegrationEvent
{
    public required string ExternalReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required string GuestName { get; init; }

    public required DateTimeOffset CheckInAt { get; init; }

    public required DateTimeOffset CheckOutAt { get; init; }

    public required int GuestCount { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
