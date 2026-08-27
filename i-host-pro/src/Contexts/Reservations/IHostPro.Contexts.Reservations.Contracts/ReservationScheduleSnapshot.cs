namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// The minimal snapshot <see cref="IReservationScheduleReader.GetScheduleAsync"/>
/// returns — mirrors <see cref="ReservationGuestContact"/>'s own minimal-shape
/// precedent (ADR-019): only what Guest Operations' Early Check-in/Late
/// Checkout decision genuinely needs, never a full Reservation projection.
/// <see cref="Status"/> is the same stable lowercase code every other public
/// surface of this Bounded Context already exposes (<c>ReservationStatusCodeMapper</c>),
/// never the raw enum.
/// </summary>
public sealed record ReservationScheduleSnapshot(
    string Status,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt);
