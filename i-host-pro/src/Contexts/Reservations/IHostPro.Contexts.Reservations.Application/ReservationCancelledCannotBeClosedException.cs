namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// Thrown by <see cref="Reservations.CloseReservationCommandHandler"/> when a
/// <c>CloseReservation</c> command targets a reservation that is already
/// <c>Cancelled</c> (Fase 10, Checkpoint 1 — Guest Operations Foundation).
/// <c>CloseReservation</c> is produced exclusively by the internal, ordered
/// Guest Operations → Workflow → Reservations checkout chain — unlike the
/// Airbnb import consumers' own external, unordered event source, a Cancelled
/// reservation receiving this command represents a genuine orchestration bug
/// or invariant violation, never an expected race. Deliberately its own
/// type, never the generic <see cref="InvalidOperationException"/> the
/// handler also uses for a genuinely missing reservation — this failure must
/// stay visible/investigable, never silently absorbed into a permanent
/// no-op, never retried by a custom policy: Wolverine's own default
/// single-attempt-then-dead-letter behavior is the only handling this
/// exception relies on.
/// </summary>
public sealed class ReservationCancelledCannotBeClosedException : Exception
{
    public ReservationCancelledCannotBeClosedException(string message) : base(message)
    {
    }
}
