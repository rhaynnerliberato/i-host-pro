namespace IHostPro.Contexts.Reservations.Domain.Enums;

/// <summary>
/// A Reservation's lifecycle state (Fase 3, Incremento 1 plan). Every
/// Reservation is born <see cref="Confirmed"/>; <see cref="Cancelled"/> is
/// terminal — no restoration exists. <see cref="Closed"/> (Fase 10,
/// Checkpoint 1 — Guest Operations Foundation) is the guest's real checkout
/// outcome, reached only from <see cref="Confirmed"/>, also terminal — no
/// restoration exists either. Any additional post-stay status (a
/// guest-did-not-arrive marker) is deliberately out of scope.
/// </summary>
public enum ReservationStatus
{
    Confirmed = 0,
    Cancelled = 1,
    Closed = 2,
}
