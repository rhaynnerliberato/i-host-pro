namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// The single, minimal synchronous query port Guest Operations may use to
/// evaluate an Early Check-in/Late Checkout request (Fase 10, Checkpoint 3;
/// ADR-024 amendment — synchronous exception #7, mirroring
/// <see cref="IReservationGuestContactReader"/>'s own shape exactly, ADR-019).
/// Guest Operations' decision must reflect the CURRENT state of Reservations'
/// own agenda — an eventually-consistent local projection could approve a
/// request against an already-stale schedule, presenting a wrong decision to
/// the guest as final. Implemented ONLY in <c>Reservations.Infrastructure</c>
/// — a consumer may reference this contract, never
/// <c>Reservations.Domain</c>/<c>Application</c>/<c>Infrastructure</c>, and
/// never <c>ReservationsDbContext</c>/the <c>reservations</c> schema
/// directly.
/// </summary>
public interface IReservationScheduleReader
{
    /// <summary>
    /// Returns <c>null</c> when no Reservation with <paramref name="reservationId"/>
    /// exists for <paramref name="tenantId"/> — a non-existent id and a
    /// cross-tenant id are indistinguishable by design, same convention as
    /// every other cross-context/cross-tenant lookup in this platform.
    /// </summary>
    Task<ReservationScheduleSnapshot?> GetScheduleAsync(
        Guid tenantId,
        Guid reservationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when another <c>Confirmed</c> reservation already exists for the
    /// SAME property as <paramref name="reservationId"/> whose interval
    /// overlaps [<paramref name="requestedCheckInAt"/>,
    /// <paramref name="requestedCheckOutAt"/>) — start inclusive, end
    /// exclusive. <paramref name="reservationId"/> itself is always excluded
    /// from its own conflict check (it is, by definition, the reservation
    /// being rescheduled) — the implementation resolves the Property
    /// internally, the caller never needs to know or pass it.
    /// </summary>
    Task<bool> HasConflictingReservationAsync(
        Guid tenantId,
        Guid reservationId,
        DateTimeOffset requestedCheckInAt,
        DateTimeOffset requestedCheckOutAt,
        CancellationToken cancellationToken);
}
