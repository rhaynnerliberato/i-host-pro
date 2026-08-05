namespace IHostPro.Contexts.Reservations.Domain.Enums;

/// <summary>
/// A Reservation's lifecycle state (Fase 3, Incremento 1 plan). Only two
/// states exist in this increment: every Reservation is born
/// <see cref="Confirmed"/>; <see cref="Cancelled"/> is terminal — no
/// restoration exists. Any additional post-stay status (a finished-stay
/// marker, a guest-did-not-arrive marker) is deliberately out of scope for
/// this increment.
/// </summary>
public enum ReservationStatus
{
    Confirmed = 0,
    Cancelled = 1,
}
